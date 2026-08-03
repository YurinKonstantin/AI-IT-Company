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
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents;

/// <summary>
/// Документатор: обходит структуру проекта и пишет документацию (README, ARCHITECTURE и т.п.).
/// Не пишет прикладной код и не участвует в сборке.
/// </summary>
public sealed class DocumenterAgent : AgentBase
{
    public override AgentRole Role => AgentRole.Documenter;
    private readonly ITranslationService _translator;

    protected override string DefaultSystemPrompt => """
    You are a Technical Documenter for C# / .NET / WinUI / MonoGame projects.

    YOUR JOB:
    1. Analyze the project structure, folders, and source files provided.
    2. Produce accurate documentation based ONLY on what you see — do not invent APIs or features.
    3. Describe architecture, modules, entry points, data flow, dependencies from .csproj.
    4. Write clear Markdown suitable for developers.

    STRICTLY FORBIDDEN:
    - Writing or rewriting application code (.cs, .xaml, .csproj).
    - Suggesting build/fix patches.
    - Inventing classes/methods that are not in the provided sources.

    REQUIRED OUTPUT — one or more Markdown blocks with paths:
    ```markdown:README.md
    # ...
    ```
    ```markdown:ARCHITECTURE.md
    # ...
    ```
    Optionally also:
    ```markdown:docs/API.md
    # ...
    ```

    README.md must include: overview, structure, how to build/run, key modules.
    ARCHITECTURE.md must include: layers, main types, dependencies, mermaid diagram if helpful.
    No text outside code blocks.
    """;

    public DocumenterAgent(
        IAiProviderFactory factory,
        AgentConfigStore configStore,
        AgentPromptStore promptStore,
        ITranslationService translator,
        ILogger<DocumenterAgent> logger)
        : base(factory, configStore, promptStore, translator, logger)
    {
        _translator = translator;
    }

    protected override string BuildUserPrompt(AgentContext ctx)
    {
        var root = ctx.OutputRoot ?? ctx.ProjectPath
            ?? PathHelper.GetProjectOutputRoot(ctx.ProjectId);

        var structure = ctx.SharedData.GetValueOrDefault("project_structure", "");
        if (string.IsNullOrWhiteSpace(structure) && Directory.Exists(root))
        {
            var scan = ProjectScanner.ScanDetailed(root);
            structure = scan.StructureTree;
            ctx.SharedData["project_structure"] = structure;
            if (ctx.Files.Count == 0)
                ctx.Files.AddRange(scan.Files);
        }

        var previews = ctx.SharedData.GetValueOrDefault("project_previews", "");
        if (string.IsNullOrWhiteSpace(previews) && ctx.Files.Count > 0)
        {
            previews = ProjectScanner.BuildFilePreviews(root, ctx.Files,
                maxFiles: 18, maxCharsPerFile: 1800, maxTotalChars: 14_000);
            ctx.SharedData["project_previews"] = previews;
        }

        var fileList = string.Join("\n",
            ctx.Files.Take(120).Select(f =>
            {
                try { return Path.GetRelativePath(root, f).Replace('\\', '/'); }
                catch { return f; }
            }));

        return $"""
            Document the existing project. User request: {ctx.UserPrompt}

            Project type: {ctx.Type}
            Mode: {ctx.Mode}
            Root: {root}
            File count: {ctx.Files.Count}

            === STRUCTURE ===
            {Truncate(structure, 4000)}

            === FILE LIST (sample) ===
            {Truncate(fileList, 3000)}

            === SOURCE PREVIEWS ===
            {Truncate(previews, 14000)}

            Produce README.md and ARCHITECTURE.md (and optional docs/API.md) as Markdown blocks with paths.
            Keep each document concise (aim under 200 lines). Base claims only on sources above.
            """;
    }

    protected override async Task<AgentResult> PostProcessAsync(AgentContext ctx, string output, CancellationToken ct)
    {
        var root = ctx.OutputRoot ?? ctx.ProjectPath
            ?? PathHelper.GetProjectOutputRoot(ctx.ProjectId);
        PathHelper.EnsureDirectory(root);

        var blocks = CodeExtractor.Extract(output)
            .Where(b => b.Path is not null && IsDocPath(b.Path))
            .ToList();

        // Fallback: если модель вернула один большой markdown без путей.
        if (blocks.Count == 0 && LooksLikeMarkdown(output))
        {
            blocks.Add(new CodeExtractor.Block("markdown", "README.md", false, UnwrapMarkdown(output)));
        }

        if (blocks.Count == 0)
        {
            var fallback = BuildFallbackDocs(ctx, output);
            blocks.Add(new CodeExtractor.Block("markdown", "README.md", false, fallback));
            Logger.LogWarning("[Documenter] LLM не вернула блоки — записан fallback README.md");
        }

        int written = 0;
        var writtenPaths = new List<string>();
        foreach (var b in blocks)
        {
            var rel = NormalizeDocPath(b.Path!);
            if (rel is null) continue;

            if (!Build.FileWriteGuard.TryResolve(root, rel, out var full, out var deny))
            {
                Logger.LogWarning("[Documenter] Запись отклонена ({Path}): {Reason}", rel, deny);
                continue;
            }

            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) PathHelper.EnsureDirectory(dir);

            await File.WriteAllTextAsync(full, b.Code.Trim() + Environment.NewLine, ct);
            written++;
            writtenPaths.Add(rel.Replace('\\', '/'));
            Logger.LogInformation("[Documenter] Записан {Path}", full);
        }

        ctx.SharedData["docs_code"] = output;
        ctx.SharedData["docs_files"] = string.Join(", ", writtenPaths);
        ctx.SharedData["docs_files_count"] = written.ToString();
        ctx.SharedData["architecture"] = blocks
            .FirstOrDefault(b => b.Path?.Contains("ARCHITECTURE", StringComparison.OrdinalIgnoreCase) == true)
            .Code
            ?? blocks.FirstOrDefault().Code
            ?? "";

        // Локализованная копия README для UI, если есть переводчик.
        var readme = blocks.FirstOrDefault(b =>
            Path.GetFileName(b.Path ?? "").Equals("README.md", StringComparison.OrdinalIgnoreCase));
        string localizedPreview = readme.Code ?? output;
        try
        {
            localizedPreview = await _translator.ToUserAsync(localizedPreview, ct);
            await File.WriteAllTextAsync(
                Path.Combine(root, "README.localized.md"), localizedPreview, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Documenter] Не удалось локализовать README");
        }

        return new AgentResult(
            written > 0,
            localizedPreview,
            written == 0 ? "Не удалось записать документацию." : null,
            Artifacts: new Dictionary<string, string>
            {
                ["docs_files"] = ctx.SharedData["docs_files"]
            });
    }

    private static bool IsDocPath(string path)
    {
        var p = path.Replace('\\', '/');
        if (p.Contains("..", StringComparison.Ordinal)) return false;
        // Только markdown-документация — не даём перезаписывать код.
        return p.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeDocPath(string path)
    {
        var p = path.Trim().TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
        if (p.Contains("..", StringComparison.Ordinal)) return null;
        if (!p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return null;
        return p;
    }

    private static bool LooksLikeMarkdown(string s)
        => s.Contains('#') || s.Contains("```");

    private static string UnwrapMarkdown(string s)
    {
        var t = s.Trim();
        if (t.StartsWith("```"))
        {
            var firstNl = t.IndexOf('\n');
            if (firstNl > 0) t = t[(firstNl + 1)..];
            if (t.EndsWith("```")) t = t[..^3];
        }
        return t.Trim();
    }

    private static string BuildFallbackDocs(AgentContext ctx, string raw)
    {
        var structure = ctx.SharedData.GetValueOrDefault("project_structure", "(structure unavailable)");
        return $"""
            # Project documentation

            ## Overview
            {ctx.SharedData.GetValueOrDefault("summary", ctx.UserPrompt)}

            ## Detected type
            {ctx.Type}

            ## Structure
            ```
            {structure}
            ```

            ## Files
            Total scanned: {ctx.Files.Count}

            > Generated in fallback mode — the model did not return valid Markdown blocks.

            ## Raw model output
            ```
            {Truncate(raw, 4000)}
            ```
            """;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "(empty)" : s.Length <= max ? s : s[..max] + "\n... (truncated)";
}
