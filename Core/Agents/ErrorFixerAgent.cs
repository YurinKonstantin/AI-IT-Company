using Core.Agents;
using Core.Configuration;
using Core.Contracts;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents;

/// <summary>
/// Исправитель ошибок. Работает в двух сценариях:
/// 1) После неудачной сборки Builder-агента — читает build_log и генерирует патч.
/// 2) В режиме FixError — анализирует ошибку из промпта пользователя и код проекта.
/// Возвращает ПОЛНЫЕ версии исправленных файлов (не diff-патчи), чтобы Builder перезаписал их.
/// </summary>
public sealed class ErrorFixerAgent : AgentBase
{
    public override AgentRole Role => AgentRole.ErrorFixer;

    protected override string DefaultSystemPrompt => """
    You are an Error Fixer. Specialist in C# / .NET / WinUI 3 / Monogame.

    ALGORITHM:
    1. Read the build log or error description from the user.
    2. Identify the ROOT cause of the problem (not the symptom): uninitialized field,
       incorrect namespace, missing NuGet package, incompatible API version, etc.
    3. Output a BRIEF analysis in a block:
       ```analysis
       Cause: ...
       Files to modify: ...
           ```
        4. Then — COMPLETE corrected versions of the files (not fragments!):
           ```csharp:Path/To/File.cs
           // full file entirely
           ```
        5. Prefer fixing CODE. The Builder already auto-runs `dotnet restore` and
           `dotnet add package` for missing NuGet packages (NU1101/CS0246 mappings).
           Only rewrite .csproj if the package reference itself is wrong/corrupt.
        6. Do not invent APIs that do not exist. If in doubt, use a proven approach.
        7. Preserve existing logic; change the minimum required to fix the issue.

    When the project is WinUI 3, also apply:
    """ + WinUi3PromptRules.SystemRules + """
    Common WinUI fix patterns:
    - System.Windows.* / Windows.UI.Xaml.* → Microsoft.UI.Xaml.*
    - Width/Height on Page → remove; set size on Window if needed
    - DataContext assignments → Page.ViewModel property + {x:Bind} + x:DataType

       PROHIBITED:
        - Returning diff-hunks (@@ ... @@).
        - Returning only class fragments.
        - Writing text outside blocks (except the ```analysis block).
    """;

    public ErrorFixerAgent(IAiProviderFactory factory, AgentConfigStore configStore, AgentPromptStore promptStore,
                        ILogger<InterpreterAgent> logger) : base(factory, configStore, promptStore, logger) { }

    protected override string BuildUserPrompt(AgentContext ctx)
    {
        var stage = ctx.CurrentStage;
        var buildLog = ctx.SharedData.GetValueOrDefault("build_log", "");
        var attempt = ctx.SharedData.GetValueOrDefault("fixer_attempt", "0");
        ctx.SharedData["fixer_attempt"] = (int.Parse(attempt) + 1).ToString();

        var stageBlock = stage is null ? "" : $"""
 === CURRENT STAGE ===
 {stage.Id}. {stage.Name}
 Deliverables: {string.Join("; ", stage.Deliverables)}
 """;

        if (ctx.Mode == WorkMode.FixError)
        {
            return $"""
 Attempt #{int.Parse(attempt) + 1}. Mode: FixError (standalone — no stage plan).
 Fix the issue described by the user and/or the build log. Change the minimum set of files.

 === User request ===
 {ctx.UserPrompt}

 === Build / error log (last 8000 chars) ===
 {TruncateTail(buildLog, 8000)}

 === Existing project code ===
 {BuildCodeContext(ctx)}

 Return:
 1) ```analysis``` block — brief diagnosis;
 2) COMPLETE versions of files that need to be rewritten.
 """;
        }

        return $"""
 Attempt #{int.Parse(attempt) + 1}. Task: return code to a compilable state within the CURRENT STAGE.
 Do not go beyond the stage scope. If an error is outside the scope, return only the affected files unchanged and indicate this in analysis.

 {stageBlock}

 === Full build log (last 6000 chars) ===
 {TruncateTail(buildLog, 6000)}

 === Code available in this stage ===
 {BuildCodeContext(ctx)}

 Return:
 1) ```analysis``` block — brief diagnosis;
 2) COMPLETE versions of files that need to be rewritten.
 """;
    }

    protected override Task<AgentResult> PostProcessAsync(AgentContext ctx, string output, CancellationToken ct)
    {
        // Извлекаем аналитический блок.
        var analysisMatch = Regex.Match(output,
            @"```analysis\s*(?<body>.*?)```", RegexOptions.Singleline);
        var analysis = analysisMatch.Success ? analysisMatch.Groups["body"].Value.Trim() : "(анализ не предоставлен)";
        ctx.SharedData["last_fix_analysis"] = analysis;

        // Ключевое: складываем исправления в отдельный слот, откуда Builder их подхватит
        // и перезапишет файлы (accumulating overlay).
        ctx.SharedData["fix_code"] = output;

        var fixedFiles = CodeExtractor.Extract(output)
            .Where(b => b.Path is not null)
            .Select(b => b.Path!)
            .Distinct()
            .ToList();

        ctx.SharedData["last_fixed_files"] = string.Join(";", fixedFiles);

        Logger.LogInformation("[ErrorFixer] Диагноз: {Analysis}", analysis);
        Logger.LogInformation("[ErrorFixer] Исправлено файлов: {Count} → {Files}",
            fixedFiles.Count, string.Join(", ", fixedFiles));

        return Task.FromResult(new AgentResult(
            Success: fixedFiles.Count > 0,
            Output: output,
            Error: fixedFiles.Count == 0 ? "Исправитель не вернул ни одного файла." : null,
            Artifacts: new Dictionary<string, string>
            {
                ["analysis"] = analysis,
                ["fixed_files"] = string.Join(";", fixedFiles)
            }));
    }

    // ---------- helpers ----------

    /// <summary>
    /// Собирает код всех известных файлов (сгенерированных агентами + прочитанных из диска)
    /// в компактный вид для передачи модели.
    /// </summary>
    private static string BuildCodeContext(AgentContext ctx)
    {
        var chunks = new List<string>();

        foreach (var key in new[] { "backend_code", "frontend_code", "game_code", "tests_code", "fix_code", "fullstack_code" })
        {
            if (ctx.SharedData.TryGetValue(key, out var blob) && !string.IsNullOrWhiteSpace(blob))
                chunks.Add($"# {key}\n{blob}");
        }

        // Если это работа с существующим проектом — подмешиваем реальные файлы с диска.
        if (!string.IsNullOrEmpty(ctx.ProjectPath) && Directory.Exists(ctx.ProjectPath))
        {
            foreach (var file in ctx.Files.Where(IsSourceFile).Take(20))
            {
                try
                {
                    var rel = Path.GetRelativePath(ctx.ProjectPath, file);
                    var content = File.ReadAllText(file);
                    var lang = file.EndsWith(".xaml") ? "xml" : "csharp";
                    chunks.Add($"```{lang}:{rel}\n{Truncate(content, 4000)}\n```");
                }
                catch { /* ignore unreadable */ }
            }
        }

        var joined = string.Join("\n\n", chunks);
        return Truncate(joined, 30_000);
    }

    private static bool IsSourceFile(string f)
        => f.EndsWith(".cs") || f.EndsWith(".xaml") || f.EndsWith(".csproj");

    /// <summary>
    /// Достаёт из лога сборки строки-ошибки формата `... error CS1234: ...`.
    /// </summary>
    private static List<string> ExtractBuildErrors(string log)
    {
        if (string.IsNullOrWhiteSpace(log)) return new();
        var rx = new Regex(@"^.*error\s+[A-Z]+\d+.*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return rx.Matches(log).Select(m => m.Value.Trim()).Distinct().Take(50).ToList();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "\n... (обрезано)";

    private static string TruncateTail(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : "... (начало обрезано)\n" + s[^max..];
}