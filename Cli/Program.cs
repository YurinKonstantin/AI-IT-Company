using Ai.Hosting;
using Build;
using Core;
using Core.Configuration;
using Core.Contracts;
using Core.Hosting;
using Core.Orchestration;
using Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Text;

namespace AiItCompany.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        if (!string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unknown command '{args[0]}'. Use: aiit run ...");
            PrintHelp();
            return 2;
        }

        var opts = CliOptions.Parse(args.Skip(1).ToArray());
        if (opts is null || string.IsNullOrWhiteSpace(opts.Prompt))
        {
            Console.Error.WriteLine("Required: --prompt \"...\"");
            PrintHelp();
            return 2;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(PathHelper.LogsRoot, "cli-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(b =>
            {
                b.ClearProviders();
                b.AddSerilog(Log.Logger, dispose: false);
            })
            .ConfigureServices((_, s) =>
            {
                s.AddAiItProviders();
                s.AddAiItPipeline();
            })
            .Build();

        var settings = host.Services.GetRequiredService<AppSettingsStore>();
        await settings.InitializeAsync();
        AiProviderServiceCollectionExtensions.ApplyProviderUrlsFromSettings(host.Services);
        await host.Services.GetRequiredService<AgentConfigStore>().InitializeAsync();
        await host.Services.GetRequiredService<AgentPromptStore>().InitializeAsync();

        var translator = host.Services.GetRequiredService<ITranslationService>();
        var translated = await translator.ToWorkingAsync(opts.Prompt);

        var projectId = Guid.NewGuid().ToString("N")[..8];
        var outputRoot = string.IsNullOrWhiteSpace(opts.Path)
            ? PathHelper.GetProjectOutputRoot(projectId)
            : PathHelper.EnsureDirectory(opts.Path);

        var (mode, locked) = ResolveMode(opts.Mode);
        var ctx = new AgentContext
        {
            ProjectId = projectId,
            UserPrompt = translated,
            ProjectPath = outputRoot,
            Mode = mode,
            ModeLocked = locked
        };

        if (Directory.Exists(outputRoot))
        {
            var scan = ProjectScanner.ScanDetailed(outputRoot);
            if (Enum.TryParse<ProjectType>(scan.Type, out var pt)) ctx.Type = pt;
            ctx.Files.AddRange(scan.Files);
            ctx.SharedData["project_structure"] = scan.StructureTree;
            if (ctx.Files.Count > 0)
                ctx.SharedData["project_metrics"] = ProjectMetrics.CollectReport(outputRoot, scan.Files);
        }

        ctx.SharedData["user_prompt_original"] = opts.Prompt;
        ctx.SharedData["ui_mode_auto"] = (!locked).ToString().ToLowerInvariant();

        var pipeline = host.Services.GetRequiredService<StagedPipeline>();
        var reader = Task.Run(async () =>
        {
            await foreach (var ev in pipeline.Events.Reader.ReadAllAsync())
            {
                var line = $"[{ev.At:HH:mm:ss}] {ev.Role}/{ev.Kind}: {Truncate(ev.Payload, 400)}";
                Console.WriteLine(line);
            }
        });

        try
        {
            await pipeline.RunAsync(ctx, CancellationToken.None);
            await reader;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Pipeline failed: " + ex.Message);
            Log.Error(ex, "CLI pipeline error");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }

        Console.WriteLine($"Done. Output: {ctx.OutputRoot ?? outputRoot}");
        return 0;
    }

    private static (WorkMode Mode, bool Locked) ResolveMode(string? mode)
    {
        var m = (mode ?? "auto").Trim().ToLowerInvariant();
        return m switch
        {
            "auto" => (WorkMode.CreateNew, false),
            "create" or "createnew" or "new" => (WorkMode.CreateNew, true),
            "improve" => (WorkMode.Improve, true),
            "fix" or "fixerror" => (WorkMode.FixError, true),
            "document" or "docs" => (WorkMode.Document, true),
            "analyze" or "analysis" => (WorkMode.Analyze, true),
            "plan" or "architecture" or "planarchitecture" => (WorkMode.PlanArchitecture, true),
            _ => (WorkMode.CreateNew, false)
        };
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            aiit — AI IT-Company CLI

            Usage:
              aiit run --prompt "..." [--mode auto|create|improve|fix|document|analyze|plan] [--path <folder>]

            Modes:
              auto       Interpreter chooses mode/type (default)
              create     CreateNew
              improve    Improve existing project
              fix        FixError
              document   Documentation only
              analyze    Analysis only
              plan       PlanArchitecture (TZ only)

            Exit codes: 0 ok, 1 pipeline error, 2 bad args
            """);
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}

internal sealed class CliOptions
{
    public string Prompt { get; init; } = "";
    public string? Path { get; init; }
    public string Mode { get; init; } = "auto";

    public static CliOptions? Parse(string[] args)
    {
        string? prompt = null;
        string? path = null;
        string mode = "auto";

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (a.ToLowerInvariant())
            {
                case "--prompt":
                case "-p":
                    prompt = Next();
                    break;
                case "--path":
                case "--project":
                    path = Next();
                    break;
                case "--mode":
                case "-m":
                    mode = Next() ?? "auto";
                    break;
                default:
                    if (prompt is null && !a.StartsWith('-'))
                        prompt = a;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(prompt)) return null;
        return new CliOptions { Prompt = prompt, Path = path, Mode = mode };
    }
}
