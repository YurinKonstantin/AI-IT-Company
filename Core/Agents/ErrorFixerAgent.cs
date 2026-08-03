using Build;
using Core.Configuration;
using Core.Contracts;
using Core.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents;

/// <summary>
/// Исправитель ошибок. Предпочитает минимальные SEARCH/REPLACE патчи;
/// полный файл — только fallback, если патч неприменим.
/// </summary>
public sealed class ErrorFixerAgent : AgentBase
{
    public const int MaxFilesPerAttempt = 5;

    public override AgentRole Role => AgentRole.ErrorFixer;

    protected override string DefaultSystemPrompt => """
    You are an Error Fixer. Specialist in C# / .NET / WinUI 3 / Monogame.

    ALGORITHM:
    1. Read the STRUCTURED error list (not the whole project).
    2. Identify the ROOT cause (uninitialized field, wrong namespace, missing using, bad API…).
    3. Output a BRIEF analysis:
       ```analysis
       Cause: ...
       Files to modify: ...
       ```
    4. Prefer MINIMAL edits via SEARCH/REPLACE (exact text from the provided file):
       ```search-replace:Path/To/File.cs
       <<<<<<< SEARCH
       exact old snippet from the file
       =======
       corrected snippet
       >>>>>>> REPLACE
       ```
       You may put several SEARCH/REPLACE pairs in one block for the same file.
    5. Full-file rewrite ONLY when the file is tiny or a patch cannot express the fix:
       ```csharp:Path/To/File.cs
       // complete file
       ```
    6. Change at most 5 files. Do not touch files that have no related errors
       (except .csproj when package references are clearly wrong).
    7. Prefer fixing CODE. Builder already runs restore / add package for missing NuGet.
    8. Preserve existing logic; do not invent APIs.

    When the project is WinUI 3, also apply:
    """ + WinUi3PromptRules.SystemRules + """
    Common WinUI fix patterns:
    - System.Windows.* / Windows.UI.Xaml.* → Microsoft.UI.Xaml.*
    - Width/Height on Page → remove; set size on Window if needed
    - DataContext assignments → Page.ViewModel property + {x:Bind} + x:DataType

    PROHIBITED:
    - Rewriting unrelated files.
    - Returning only class fragments without SEARCH/REPLACE markers.
    - Writing text outside fenced blocks (except ```analysis).
    """;

    public ErrorFixerAgent(IAiProviderFactory factory, AgentConfigStore configStore, AgentPromptStore promptStore,
                        CorrectionLessonStore lessons, AppSettingsStore appSettings,
                        IWebSearchService webSearch,
                        ILogger<InterpreterAgent> logger)
        : base(factory, configStore, promptStore, logger, lessons, appSettings, webSearch) { }

    protected override string BuildUserPrompt(AgentContext ctx)
    {
        var stage = ctx.CurrentStage;
        var buildLog = ctx.SharedData.GetValueOrDefault("build_log", "");
        var attempt = ctx.SharedData.GetValueOrDefault("fixer_attempt", "0");
        ctx.SharedData["fixer_attempt"] = (int.Parse(attempt) + 1).ToString();

        var errors = BuildErrorParser.Parse(buildLog);
        var errorBlock = BuildErrorParser.FormatForPrompt(errors);
        var errorFiles = BuildErrorParser.DistinctRelativeFiles(errors, ctx.ProjectPath ?? ctx.OutputRoot);
        ctx.SharedData["build_error_files"] = string.Join(";", errorFiles);

        var stageBlock = stage is null ? "" : $"""
 === CURRENT STAGE ===
 {stage.Id}. {stage.Name}
 Deliverables: {string.Join("; ", stage.Deliverables)}
 """;

        var codeCtx = BuildCodeContext(ctx, errorFiles);

        if (ctx.Mode == WorkMode.FixError)
        {
            return $"""
 Attempt #{int.Parse(attempt) + 1}. Mode: FixError (standalone — no stage plan).
 Fix with MINIMAL SEARCH/REPLACE patches. At most {MaxFilesPerAttempt} files.

 === User request ===
 {ctx.UserPrompt}

 === STRUCTURED ERRORS ===
 {errorBlock}

 === Build log tail (reference) ===
 {TruncateTail(buildLog, 3000)}

 === Files with errors (and nearby context) ===
 {codeCtx}

 Return:
 1) ```analysis``` block;
 2) ```search-replace:path``` blocks (preferred) or complete files only as fallback.
 """;
        }

        return $"""
 Attempt #{int.Parse(attempt) + 1}. Return the CURRENT STAGE to a compilable state.
 Do not go beyond stage scope. Prefer SEARCH/REPLACE. At most {MaxFilesPerAttempt} files.

 {stageBlock}

 === STRUCTURED ERRORS ===
 {errorBlock}

 === Build log tail (reference) ===
 {TruncateTail(buildLog, 2500)}

 === Files with errors ===
 {codeCtx}

 Return:
 1) ```analysis``` block;
 2) ```search-replace:path``` blocks (preferred) or complete files only as fallback.
 """;
    }

    protected override Task<AgentResult> PostProcessAsync(AgentContext ctx, string output, CancellationToken ct)
    {
        var analysisMatch = Regex.Match(output,
            @"```analysis\s*(?<body>.*?)```", RegexOptions.Singleline);
        var analysis = analysisMatch.Success ? analysisMatch.Groups["body"].Value.Trim() : "(анализ не предоставлен)";
        ctx.SharedData["last_fix_analysis"] = analysis;

        var allowed = ctx.SharedData.GetValueOrDefault("build_error_files", "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var blocks = CodeExtractor.Extract(output)
            .Where(b => b.Path is not null)
            .ToList();

        // Guardrail: drop files beyond max / outside error set (allow .csproj).
        var kept = new List<CodeExtractor.Block>();
        foreach (var b in blocks)
        {
            var path = b.Path!.Replace('\\', '/');
            var isCsproj = path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
            if (allowed.Count > 0 && !isCsproj && !IsPathAllowed(path, allowed))
            {
                Logger.LogWarning("[ErrorFixer] Skip {Path} — not in error file list", path);
                continue;
            }
            kept.Add(b);
            if (kept.Count >= MaxFilesPerAttempt) break;
        }

        // Rebuild fix_code from kept blocks only (preserve analysis for UI).
        var filtered = RebuildFixBlob(analysis, kept);
        ctx.SharedData["fix_code"] = filtered;

        var fixedFiles = kept.Select(b => b.Path!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ctx.SharedData["last_fixed_files"] = string.Join(";", fixedFiles);

        Logger.LogInformation("[ErrorFixer] Диагноз: {Analysis}", analysis);
        Logger.LogInformation("[ErrorFixer] Исправлено файлов: {Count} → {Files}",
            fixedFiles.Count, string.Join(", ", fixedFiles));

        return Task.FromResult(new AgentResult(
            Success: fixedFiles.Count > 0,
            Output: filtered,
            Error: fixedFiles.Count == 0 ? "Исправитель не вернул ни одного файла." : null,
            Artifacts: new Dictionary<string, string>
            {
                ["analysis"] = analysis,
                ["fixed_files"] = string.Join(";", fixedFiles)
            }));
    }

    private static bool IsPathAllowed(string path, HashSet<string> allowed)
    {
        if (allowed.Contains(path)) return true;
        var name = Path.GetFileName(path);
        return allowed.Any(a =>
            a.Equals(path, StringComparison.OrdinalIgnoreCase)
            || a.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(a).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string RebuildFixBlob(string analysis, List<CodeExtractor.Block> blocks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("```analysis");
        sb.AppendLine(analysis);
        sb.AppendLine("```");
        sb.AppendLine();
        foreach (var b in blocks)
        {
            var lang = string.IsNullOrWhiteSpace(b.Lang) ? "csharp" : b.Lang;
            if (b.IsDiff) lang += ":diff";
            sb.Append("```").Append(lang).Append(':').Append(b.Path).AppendLine();
            sb.AppendLine(b.Code.TrimEnd());
            sb.AppendLine("```");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Prefer files named in structured errors; fall back to a small disk sample.
    /// </summary>
    private static string BuildCodeContext(AgentContext ctx, IReadOnlyList<string> errorFiles)
    {
        var chunks = new List<string>();
        var root = ctx.ProjectPath ?? ctx.OutputRoot;

        foreach (var key in new[] { "backend_code", "frontend_code", "game_code", "tests_code", "fullstack_code" })
        {
            if (ctx.SharedData.TryGetValue(key, out var blob) && !string.IsNullOrWhiteSpace(blob))
                chunks.Add($"# {key}\n{Truncate(blob, 6000)}");
        }

        if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
        {
            var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rel in errorFiles.Take(MaxFilesPerAttempt))
            {
                var full = TryResolve(root, rel);
                if (full is null || !loaded.Add(full)) continue;
                TryAddFile(chunks, root, full);
            }

            // If no structured files, take a few source files from ctx.Files that look relevant.
            if (loaded.Count == 0)
            {
                foreach (var file in ctx.Files.Where(IsSourceFile).Take(8))
                {
                    if (!loaded.Add(file)) continue;
                    TryAddFile(chunks, root, file);
                }
            }
        }

        var joined = string.Join("\n\n", chunks);
        return Truncate(joined, 24_000);
    }

    private static string? TryResolve(string root, string rel)
    {
        try
        {
            var candidate = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(candidate)) return candidate;

            // Match by file name anywhere under known files list is handled by caller via ctx.Files.
            var name = Path.GetFileName(rel);
            var matches = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories)
                .Where(f =>
                {
                    var n = Path.GetFileName(Path.GetDirectoryName(f) ?? "");
                    return !n.Equals("bin", StringComparison.OrdinalIgnoreCase)
                        && !n.Equals("obj", StringComparison.OrdinalIgnoreCase);
                })
                .Take(1)
                .ToList();
            return matches.FirstOrDefault();
        }
        catch { return null; }
    }

    private static void TryAddFile(List<string> chunks, string root, string full)
    {
        try
        {
            var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
            var content = File.ReadAllText(full);
            var lang = full.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ? "xml"
                : full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? "xml"
                : "csharp";
            // Keep full file when reasonably small so SEARCH can match exactly.
            chunks.Add($"```{lang}:{rel}\n{Truncate(content, 12_000)}\n```");
        }
        catch { /* ignore */ }
    }

    private static bool IsSourceFile(string f)
        => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
        || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
        || f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "\n... (обрезано)";

    private static string TruncateTail(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : "... (начало обрезано)\n" + s[^max..];
}
