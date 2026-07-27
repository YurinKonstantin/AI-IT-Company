using Core.Agents;
using Core.Configuration;
using Core.Contracts;
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

    protected override string SystemPrompt => """
        Ты — Секретарь. Пишешь профессиональный итоговый отчёт на РУССКОМ языке в Markdown.

        СТРУКТУРА ОТЧЁТА (соблюдай ЖЁСТКО):
        # Итоговый отчёт по проекту

        ## 1. Описание
        Краткое (3–5 предложений) описание того, что было сделано.

        ## 2. Режим работы
        Создание / улучшение / исправление / документирование.

        ## 3. Архитектура
        Список ключевых модулей и их назначение (буллеты).

        ## 4. Выполненные этапы
        Таблица: | Агент | Действие | Результат |

        ## 5. Статистика
        - Всего файлов: ...
        - Файлов с тестами: ...
        - Попыток авто-исправления: ...
        - Итог сборки: успех / провал.

        ## 6. Инструкция по запуску
        Пошагово: dotnet restore → dotnet build → dotnet run / dotnet test.

        ## 7. Известные ограничения и рекомендации
        Что ещё стоит улучшить.

        ФОРМАТ ОТВЕТА:
        Верни один блок:
        ```markdown:REPORT.md
        # ... полный отчёт ...
        ```
        Никакого другого текста.
    """;

    public SecretaryAgent(IAiProviderFactory factory, AgentConfigStore configStore,
                        ILogger<InterpreterAgent> logger) : base(factory, configStore, logger) { }

    protected override string BuildUserPrompt(AgentContext ctx)
    {
        // Готовим "сухие" факты. Модель на их основе напишет красивый отчёт.
        var facts = CollectFacts(ctx);

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
        // Извлекаем markdown-блок отчёта.
        var reportBlock = CodeExtractor.Extract(output)
            .FirstOrDefault(b => b.Path?.EndsWith("REPORT.md", StringComparison.OrdinalIgnoreCase) == true);

        string reportMarkdown = reportBlock.Code ?? BuildFallbackReport(ctx, output);

        ctx.SharedData["final_report"] = reportMarkdown;

        // Сохраняем отчёт на диск рядом с проектом.
        var projectRoot = ctx.ProjectPath ?? Path.Combine("Output", ctx.ProjectId);
        try
        {
            Directory.CreateDirectory(projectRoot);
            var path = Path.Combine(projectRoot, "REPORT.md");
            await File.WriteAllTextAsync(path, reportMarkdown, ct);
            Logger.LogInformation("[Secretary] Отчёт сохранён: {Path}", path);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Secretary] Не удалось сохранить REPORT.md");
        }

        return new AgentResult(
            Success: true,
            Output: reportMarkdown,
            Artifacts: new Dictionary<string, string> { ["report_path"] = "REPORT.md" });
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
            ("fix_code", "Fixes")
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