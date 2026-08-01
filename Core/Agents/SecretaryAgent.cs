using Core.Agents;
using Core.Configuration;
using Core.Contracts;
using Core.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents;

/// <summary>
/// Секретарь. Формирует итоговый отчёт в Markdown:
/// - краткое описание проекта,
/// - что сделали агенты,
/// - статистика (файлы, тесты, попытки исправления),
/// - результат сборки,
/// - инструкции по запуску.
/// Отчёт сохраняется в корень проекта как REPORT.md.
/// </summary>
public sealed class SecretaryAgent : AgentBase
{
    public override AgentRole Role => AgentRole.Secretary;
    ITranslationService _translator;
    protected override string DefaultSystemPrompt => """
    You are a Secretary. Write a professional final report in Markdown.

    REPORT STRUCTURE (strictly follow):
    # Final Project Report

    ## 1. Description
    Brief (3–5 sentences) description of what was done.

    ## 2. Operating Mode
    Creation / improvement / correction / documentation.

    ## 3. Architecture
    List of key modules and their purpose (bullets).

    ## 4. Completed Stages
    Table: | Agent | Action | Result |

    ## 5. Statistics
    - Total files: ...
    - Files with tests: ...
    - Auto-correction attempts: ...
    - Build result: success / failure.

    ## 6. Launch Instructions
    Step by step: dotnet restore → dotnet build → dotnet run / dotnet test.

    ## 7. Known Limitations and Recommendations
    What else could be improved.

    RESPONSE FORMAT:
    Return one block:
    ```markdown:REPORT.md
    # ... full report ...
        ```
       No other text.
    """;

    public SecretaryAgent(IAiProviderFactory factory, AgentConfigStore configStore, AgentPromptStore promptStore, ITranslationService translator,
                        ILogger<InterpreterAgent> logger) : base(factory, configStore, promptStore, translator, logger) { _translator = translator; }

    protected override string BuildUserPrompt(AgentContext ctx)
    {
        // Готовим "сухие" факты. Модель на их основе напишет красивый отчёт.
        var facts = CollectFacts(ctx);
        Debug.WriteLine("AgentResult " + $"""
            Составь итоговый отчёт по следующим фактам.
            Не выдумывай ничего, чего нет в фактах.

            === Промпт пользователя ===
            {ctx.UserPrompt}

            === Факты проекта ===
            {facts}

            === Архитектура (from Architect) ===
            {Truncate(ctx.SharedData.GetValueOrDefault("architecture", ""), 4000)}

            === Диагноз последнего исправления (если было) ===
            {ctx.SharedData.GetValueOrDefault("last_fix_analysis", "(исправления не понадобились)")}
            """);
        return $"""
            Составь итоговый отчёт по следующим фактам.
            Не выдумывай ничего, чего нет в фактах.

            === Промпт пользователя ===
            {ctx.UserPrompt}

            === Факты проекта ===
            {facts}

            === Архитектура (from Architect) ===
            {Truncate(ctx.SharedData.GetValueOrDefault("architecture", ""), 4000)}

            === Диагноз последнего исправления (если было) ===
            {ctx.SharedData.GetValueOrDefault("last_fix_analysis", "(исправления не понадобились)")}
            """;
    }

    protected override async Task<AgentResult> PostProcessAsync(
       AgentContext ctx, string output, CancellationToken ct)
    {
        var reportBlock = CodeExtractor.Extract(output)
            .FirstOrDefault(b => b.Path?.EndsWith("REPORT.md", StringComparison.OrdinalIgnoreCase) == true);

        string reportMarkdown = reportBlock.Code ?? BuildFallbackReport(ctx, output);
        ctx.SharedData["final_report"] = reportMarkdown;

        var projectRoot = ctx.OutputRoot
                       ?? ctx.ProjectPath
                       ?? PathHelper.GetProjectOutputRoot(ctx.ProjectId);
        PathHelper.EnsureDirectory(projectRoot);

        try
        {
            var path = Path.Combine(projectRoot, "REPORT.md");

            ctx.SharedData["report_path"] = path;
            Logger.LogInformation("[Secretary] Отчёт сохранён: {Path}", path);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Secretary] Не удалось сохранить REPORT.md");
        }

        var reportLocalized = await _translator.ToUserAsync(reportMarkdown, ct);
        ctx.SharedData["final_report_original"] = reportMarkdown;
        ctx.SharedData["final_report_localized"] = reportLocalized;

        // Сохраняем оба файла:
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "REPORT.md"), reportMarkdown, ct);
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "REPORT.localized.md"), reportLocalized, ct);

        return new AgentResult(true, reportLocalized, Artifacts: new Dictionary<string, string> { ["report_path"] = projectRoot });
    }

    // ---------- helpers ----------

    private static string CollectFacts(AgentContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"- ProjectId: {ctx.ProjectId}");
        sb.AppendLine($"- Режим: {ctx.Mode}");
        sb.AppendLine($"- Тип проекта: {ctx.Type}");
        sb.AppendLine($"- Путь: {ctx.ProjectPath ?? "(новый, ./Output/" + ctx.ProjectId + ")"}");
        sb.AppendLine($"- Файлов на входе: {ctx.Files.Count}");

        int totalGenerated = 0;
        foreach (var (key, label) in new[]
        {
            ("backend_code", "Backend"),
            ("frontend_code", "Frontend"),
            ("game_code", "Game"),
            ("tests_code", "Tests"),
            ("fix_code", "Fixes"),
            ("fullstack_code", "fullstack")
        })
        {
            if (!ctx.SharedData.TryGetValue(key, out var blob)) continue;
            var count = CodeExtractor.Extract(blob).Count(b => b.Path is not null);
            totalGenerated += count;
            sb.AppendLine($"- {label}: {count} файл(ов)");
        }

        sb.AppendLine($"- Всего сгенерировано/изменено файлов: {totalGenerated}");
        sb.AppendLine($"- Тестовых файлов: {ctx.SharedData.GetValueOrDefault("tests_files_count", "0")}");
        sb.AppendLine($"- Попыток авто-исправления: {ctx.SharedData.GetValueOrDefault("fixer_attempt", "0")}");
        sb.AppendLine($"- Сборка: {(ctx.SharedData.GetValueOrDefault("build_ok", "unknown") == "true" ? "✅ успех" : "❌ провал")}");

        var fixedFiles = ctx.SharedData.GetValueOrDefault("last_fixed_files", "");
        if (!string.IsNullOrWhiteSpace(fixedFiles))
            sb.AppendLine($"- Последние исправленные файлы: {fixedFiles}");

        return sb.ToString();
    }

    /// <summary>
    /// На случай, если LLM вернула невалидный формат — собираем отчёт вручную из фактов.
    /// </summary>
    private static string BuildFallbackReport(AgentContext ctx, string rawOutput)
    {
        var facts = CollectFacts(ctx);
        return $"""
            # Итоговый отчёт по проекту

            ## 1. Описание
            {ctx.SharedData.GetValueOrDefault("summary", ctx.UserPrompt)}

            ## 2. Режим работы
            {ctx.Mode}

            ## 3. Факты
            {facts}

            ## 4. Сырый ответ агента
            ```
            {rawOutput}
            ```

            > ⚠️ Отчёт собран в fallback-режиме: LLM не вернула валидный Markdown-блок.
            """;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "(нет данных)" : s.Length <= max ? s : s[..max] + "\n... (обрезано)";
}