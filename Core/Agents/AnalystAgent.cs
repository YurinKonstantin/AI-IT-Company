using Build;
using Core.Configuration;
using Core.Contracts;
using Core.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents;

/// <summary>
/// Аналитик: оценивает существующий проект без изменения кода.
/// Пишет ANALYSIS.md (метрики, запахи, риски, рекомендации).
/// </summary>
public sealed class AnalystAgent : AgentBase
{
    public override AgentRole Role => AgentRole.Analyst;
    private readonly ITranslationService _translator;

    protected override string DefaultSystemPrompt => """
    You are a Software Analyst for C# / .NET / WinUI / MonoGame projects.

    YOUR JOB:
    - Analyze structure, metrics, and source previews.
    - Identify architecture, code smells, risks, missing tests/docs, dependency issues.
    - Give prioritized, actionable recommendations.
    - Do NOT invent classes/APIs that are not evidenced by the provided material.

    STRICTLY FORBIDDEN:
    - Writing or rewriting application source code (.cs, .xaml, .csproj).
    - Producing patches or build fixes.

    REQUIRED OUTPUT — one Markdown block:
    ```markdown:ANALYSIS.md
    # Project Analysis
    ## 1. Overview
    ## 2. Structure
    ## 3. Metrics interpretation
    ## 4. Strengths
    ## 5. Code smells & risks
    ## 6. Recommendations (prioritized)
    ## 7. Suggested next steps for Improve/Fix modes
    ```
    No text outside the block.
    """;

    public AnalystAgent(
        IAiProviderFactory factory,
        AgentConfigStore configStore,
        AgentPromptStore promptStore,
        ITranslationService translator,
        ILogger<AnalystAgent> logger)
        : base(factory, configStore, promptStore, translator, logger)
    {
        _translator = translator;
    }

    protected override string BuildUserPrompt(AgentContext ctx)
    {
        var root = ctx.OutputRoot ?? ctx.ProjectPath
            ?? PathHelper.GetProjectOutputRoot(ctx.ProjectId);

        var metrics = ctx.SharedData.GetValueOrDefault("project_metrics", "");
        if (string.IsNullOrWhiteSpace(metrics) && Directory.Exists(root))
        {
            metrics = ProjectMetrics.CollectReport(root, ctx.Files);
            ctx.SharedData["project_metrics"] = metrics;
        }

        var structure = ctx.SharedData.GetValueOrDefault("project_structure", "");
        var previews = ctx.SharedData.GetValueOrDefault("project_previews", "");
        if (string.IsNullOrWhiteSpace(previews) && ctx.Files.Count > 0)
        {
            previews = ProjectScanner.BuildFilePreviews(root, ctx.Files, maxFiles: 20, maxTotalChars: 28000);
            ctx.SharedData["project_previews"] = previews;
        }

        return $"""
            Analyze this existing project. User focus: {ctx.UserPrompt}

            Type: {ctx.Type}
            Root: {root}
            Files: {ctx.Files.Count}

            === METRICS ===
            {Truncate(metrics, 8000)}

            === STRUCTURE ===
            {Truncate(structure, 8000)}

            === SOURCE PREVIEWS ===
            {Truncate(previews, 28000)}
            """;
    }

    protected override async Task<AgentResult> PostProcessAsync(AgentContext ctx, string output, CancellationToken ct)
    {
        var root = ctx.OutputRoot ?? ctx.ProjectPath
            ?? PathHelper.GetProjectOutputRoot(ctx.ProjectId);
        PathHelper.EnsureDirectory(root);

        var block = CodeExtractor.Extract(output)
            .FirstOrDefault(b => b.Path is not null
                && b.Path.EndsWith("ANALYSIS.md", StringComparison.OrdinalIgnoreCase));

        string markdown = block.Code;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            // Любой .md или сырой текст
            var anyMd = CodeExtractor.Extract(output).FirstOrDefault(b =>
                b.Path?.EndsWith(".md", StringComparison.OrdinalIgnoreCase) == true);
            markdown = !string.IsNullOrWhiteSpace(anyMd.Code)
                ? anyMd.Code
                : BuildFallback(ctx, output);
        }

        var path = Path.Combine(root, "ANALYSIS.md");
        await File.WriteAllTextAsync(path, markdown.Trim() + Environment.NewLine, ct);

        string localized = markdown;
        try
        {
            localized = await _translator.ToUserAsync(markdown, ct);
            await File.WriteAllTextAsync(Path.Combine(root, "ANALYSIS.localized.md"), localized, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Analyst] Локализация не удалась");
        }

        ctx.SharedData["analysis_report"] = markdown;
        ctx.SharedData["analysis_path"] = path;
        ctx.SharedData["architecture"] = markdown; // для Secretary

        Logger.LogInformation("[Analyst] Сохранён {Path}", path);
        return new AgentResult(true, localized, Artifacts: new Dictionary<string, string>
        {
            ["analysis_path"] = path
        });
    }

    private static string BuildFallback(AgentContext ctx, string raw)
        => $"""
            # Project Analysis

            ## Overview
            {ctx.SharedData.GetValueOrDefault("summary", ctx.UserPrompt)}

            ## Metrics
            ```
            {ctx.SharedData.GetValueOrDefault("project_metrics", "(none)")}
            ```

            ## Structure
            ```
            {Truncate(ctx.SharedData.GetValueOrDefault("project_structure", ""), 4000)}
            ```

            > Fallback: model did not return a valid ANALYSIS.md block.

            ## Raw output
            ```
            {Truncate(raw, 3000)}
            ```
            """;

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "(empty)" : s.Length <= max ? s : s[..max] + "\n…";
}
