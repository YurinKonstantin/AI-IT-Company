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
/// Лёгкий UX-ревьюер WinUI/MAUI: читает XAML, пишет UX_REVIEW.md, код не меняет.
/// </summary>
public sealed class UxReviewerAgent : AgentBase
{
    public override AgentRole Role => AgentRole.UxReviewer;
    private readonly ITranslationService _translator;

    protected override string DefaultSystemPrompt => """
    You are a UX Reviewer for WinUI 3 / .NET MAUI desktop and mobile UIs.

    YOUR JOB:
    - Review XAML and related code-behind for usability, accessibility, layout, navigation, and consistency.
    - Produce actionable recommendations prioritized by impact.
    - Base findings ONLY on provided XAML/source — do not invent controls that are not present.

    STRICTLY FORBIDDEN:
    - Writing or rewriting application code (.cs, .xaml, .csproj).
    - Producing patches or "fixed" XAML blocks meant to replace files.

    REQUIRED OUTPUT — one Markdown block:
    ```markdown:UX_REVIEW.md
    # UX Review
    ## 1. Summary
    ## 2. Strengths
    ## 3. Issues (priority: High / Medium / Low)
    ## 4. Accessibility
    ## 5. Layout & responsiveness
    ## 6. Recommendations (next Improve steps)
    ```
    No text outside the block.
    """;

    public UxReviewerAgent(
        IAiProviderFactory factory,
        AgentConfigStore configStore,
        AgentPromptStore promptStore,
        ITranslationService translator,
        ILogger<UxReviewerAgent> logger)
        : base(factory, configStore, promptStore, translator, logger)
    {
        _translator = translator;
    }

    protected override string BuildUserPrompt(AgentContext ctx)
    {
        var root = ctx.OutputRoot ?? ctx.ProjectPath
            ?? PathHelper.GetProjectOutputRoot(ctx.ProjectId);

        if (ctx.Files.Count == 0 && Directory.Exists(root))
        {
            var scan = ProjectScanner.ScanDetailed(root);
            ctx.Files.AddRange(scan.Files);
            ctx.SharedData["project_structure"] = scan.StructureTree;
        }

        var xamlFiles = ctx.Files
            .Where(f => f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Take(40)
            .ToList();

        var previews = BuildXamlPreviews(root, xamlFiles, maxTotalChars: 36000);
        var list = string.Join("\n", xamlFiles.Select(f =>
        {
            try { return Path.GetRelativePath(root, f).Replace('\\', '/'); }
            catch { return f; }
        }));

        return $"""
            Review UX of this UI project. User task context: {ctx.UserPrompt}

            Project type: {ctx.Type}
            Mode: {ctx.Mode}
            Root: {root}

            === XAML FILES ===
            {Truncate(list, 6000)}

            === XAML PREVIEWS ===
            {Truncate(previews, 36000)}

            Write UX_REVIEW.md as a single markdown block with path.
            """;
    }

    protected override async Task<AgentResult> PostProcessAsync(AgentContext ctx, string output, CancellationToken ct)
    {
        var root = ctx.OutputRoot ?? ctx.ProjectPath
            ?? PathHelper.GetProjectOutputRoot(ctx.ProjectId);
        PathHelper.EnsureDirectory(root);

        var blocks = CodeExtractor.Extract(output)
            .Where(b => b.Path is not null
                        && Path.GetFileName(b.Path).Equals("UX_REVIEW.md", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (blocks.Count == 0 && LooksLikeMarkdown(output))
            blocks.Add(new CodeExtractor.Block("markdown", "UX_REVIEW.md", false, UnwrapMarkdown(output)));

        if (blocks.Count == 0)
        {
            blocks.Add(new CodeExtractor.Block("markdown", "UX_REVIEW.md", false,
                BuildFallback(ctx, output)));
            Logger.LogWarning("[UxReviewer] LLM не вернула блок — записан fallback UX_REVIEW.md");
        }

        var body = blocks[0].Code.Trim() + Environment.NewLine;
        if (!FileWriteGuard.TryResolve(root, "UX_REVIEW.md", out var full, out var deny))
            return new AgentResult(false, output, deny ?? "UX_REVIEW.md write denied");

        await File.WriteAllTextAsync(full, body, ct);

        try
        {
            var localized = await _translator.ToUserAsync(body, ct);
            await File.WriteAllTextAsync(Path.Combine(root, "UX_REVIEW.localized.md"), localized, ct);
        }
        catch
        {
            /* optional */
        }

        ctx.SharedData["ux_review"] = body;
        ctx.SharedData["ux_review_path"] = "UX_REVIEW.md";

        return new AgentResult(true, "UX_REVIEW.md written",
            Artifacts: new Dictionary<string, string> { ["ux_review"] = "UX_REVIEW.md" });
    }

    private static string BuildXamlPreviews(string root, List<string> files, int maxTotalChars)
    {
        var sb = new StringBuilder();
        int used = 0;
        const int perFile = 4000;

        foreach (var f in files)
        {
            if (used >= maxTotalChars) break;
            try
            {
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                var text = File.ReadAllText(f);
                if (text.Length > perFile)
                    text = text[..perFile] + "\n...(обрезано)";
                sb.AppendLine($"```xml:{rel}");
                sb.AppendLine(text);
                sb.AppendLine("```");
                sb.AppendLine();
                used += text.Length;
            }
            catch { /* skip */ }
        }

        return sb.Length == 0 ? "(no XAML found)" : sb.ToString();
    }

    private static string BuildFallback(AgentContext ctx, string raw) => $"""
        # UX Review

        ## 1. Summary
        Автоматический fallback: модель не вернула структурированный отчёт.
        Тип проекта: {ctx.Type}. Файлов в контексте: {ctx.Files.Count}.

        ## 2. Strengths
        - (не определено)

        ## 3. Issues
        - Проверьте иерархию NavigationView / страниц вручную.

        ## 4. Accessibility
        - Убедитесь, что у интерактивных элементов есть AutomationProperties.Name.

        ## 5. Layout & responsiveness
        - Проверьте адаптив на узких окнах.

        ## 6. Recommendations
        - Перезапустите режим Improve с запросом «исправить UX по UX_REVIEW».

        ### Raw model output
        {Truncate(raw, 2000)}
        """;

    private static bool LooksLikeMarkdown(string s)
        => !string.IsNullOrWhiteSpace(s)
           && (s.Contains('#') || s.Contains("## ") || s.Contains("UX", StringComparison.OrdinalIgnoreCase));

    private static string UnwrapMarkdown(string s)
    {
        var t = s.Trim();
        if (t.StartsWith("```"))
        {
            var nl = t.IndexOf('\n');
            if (nl > 0) t = t[(nl + 1)..];
            if (t.EndsWith("```")) t = t[..^3];
        }
        return t.Trim();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "(none)" : s.Length <= max ? s : s[..max] + "\n…";
}
