
using Core.Agents;
using Core.Configuration;
using Core.Contracts;
using Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents;

public sealed class ArchitectAgent : AgentBase
{
    public override AgentRole Role => AgentRole.Architect;

    protected override string DefaultSystemPrompt => """
        Ты — Архитектор. Твоя задача — составить ТЕХНИЧЕСКОЕ ЗАДАНИЕ и ПЛАН ЭТАПОВ.
        КАТЕГОРИЧЕСКИ ЗАПРЕЩЕНО:
        - Писать код (никаких C#, XAML, JSON-кода приложения, csproj и т.п.).
        - Указывать конкретные имена файлов, классов, методов, переменных.
        - Указывать конкретные библиотеки или NuGet-пакеты (это решает Кодер).

        ЧТО НУЖНО СДЕЛАТЬ:
        1. Понять задачу пользователя.
        2. Сформулировать функциональные возможности с описанием и преимуществами для пользователя.
        3. Составить нефункциональные требования (производительность, надёжность, UX и т.д.).
        4. Разбить работу на ЭТАПЫ так, чтобы:
           - каждый этап оставлял РАБОЧУЮ программу (пусть с урезанным функционалом),
           - явно указывалось, от каких других этапов он зависит,
           - были критерии приёмки, по которым можно проверить успех.

               СТРОГО:  все ключи И значения-строки — В ДВОЙНЫХ КАВЫЧКАХ. Никаких //-комментариев,
    никаких висящих запятых, никаких одинарных кавычек. Ответ — ровно один JSON-объект,
    без markdown-обёртки.

        ФОРМАТ ОТВЕТА — СТРОГО JSON (без markdown-обёрток, без пояснений):
        {
          "title": "...",
          "summary": "2–4 предложения о проекте",
          "features": [
            {"name":"...","description":"...","advantage":"выгода для пользователя"}
          ],
          "nonFunctionalRequirements": ["..."],
          "constraints": ["..."],
          "stages": [
            {
              "id": "S1",
              "name": "Минимально жизнеспособная версия",
              "goal": "Что должно работать после этапа",
              "deliverables": ["функциональные пункты, БЕЗ упоминания файлов/классов"],
              "acceptanceCriteria": ["..."],
              "dependsOn": [],
              "scope": ["Backend","Frontend"]
            }
          ]
        }

        ПРАВИЛА ДЛЯ ЭТАПОВ:
        - S1 всегда независим (dependsOn: []) и даёт минимальный работающий результат.
        - Каждый следующий этап опирается на предыдущие только там, где это действительно нужно.
        - Старайся делать этапы независимыми, где возможно — это важно для отказоустойчивости.
        - scope принимает значения: "Backend", "Frontend", "Game", "Tests", "Docs".
    """;

    public ArchitectAgent(IAiProviderFactory factory, AgentConfigStore configStore, AgentPromptStore promptStore,
                          ILogger<ArchitectAgent> logger)
        : base(factory, configStore, promptStore, logger) { }

    protected override string BuildUserPrompt(AgentContext ctx)
    {
        var files = ctx.Files.Count > 0 ? string.Join("\n", ctx.Files.Take(50)) : "(новый проект)";
        return $"""
            Задача от пользователя: {ctx.UserPrompt}
            Тип проекта: {ctx.Type}
            Режим: {ctx.Mode}

            Существующие файлы:
            {files}

            Составь ТЗ и план этапов в требуемом JSON-формате.
            """;
    }

    protected override async Task<AgentResult> PostProcessAsync(
        AgentContext ctx, string output, CancellationToken ct)
    {
        try
        {
            var raw = ExtractJson(output);

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,                       // ← добавили
                ReadCommentHandling = JsonCommentHandling.Skip    // ← добавили
            };

            ProjectPlan? plan = null;
            Exception? lastError = null;

            // 1) Пробуем как есть.
            try { plan = JsonSerializer.Deserialize<ProjectPlan>(raw, options); }
            catch (JsonException ex) { lastError = ex; }

            // 2) Если не вышло — санитайзим и пробуем снова.
            if (plan is null)
            {
                var cleaned = SanitizeJson(raw);
                try { plan = JsonSerializer.Deserialize<ProjectPlan>(cleaned, options); }
                catch (JsonException ex)
                {
                    // Дампим для отладки, чтобы вы могли прислать сырой ответ.
                    DumpBadJson(ctx, output, raw, cleaned, ex);
                    throw;
                }
                raw = cleaned;
            }

            if (plan is null) throw new InvalidOperationException("Пустой JSON от архитектора.");

            ValidatePlan(plan);
            ctx.SharedData["project_plan_json"] = raw;
            ctx.Plan = plan;

            var tzMarkdown = RenderTzMarkdown(plan);
            ctx.SharedData["tz_markdown"] = tzMarkdown;
            await SaveTzAsync(ctx, tzMarkdown, ct);

            Logger.LogInformation("[Architect] План: {StageCount} этапов, {FeatureCount} функций.",
                plan.Stages.Count, plan.Features.Count);

            return new AgentResult(true, tzMarkdown);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Architect] Не удалось разобрать план: {Msg}", ex.Message);
            return new AgentResult(false, output, ex.Message);
        }
    }

    private static void ValidatePlan(ProjectPlan plan)
    {
        if (plan.Stages.Count == 0)
            throw new InvalidOperationException("План не содержит этапов.");
        var ids = plan.Stages.Select(s => s.Id).ToHashSet();
        foreach (var stage in plan.Stages)
            foreach (var dep in stage.DependsOn)
                if (!ids.Contains(dep))
                    throw new InvalidOperationException(
                        $"Этап {stage.Id} зависит от несуществующего {dep}.");
        // Проверка цикла.
        DetectCycles(plan.Stages);
    }
    private static void DumpBadJson(AgentContext ctx, string original, string extracted, string cleaned, Exception ex)
    {
        try
        {
            var root = ctx.OutputRoot
                    ?? ctx.ProjectPath
                    ?? PathHelper.GetProjectOutputRoot(ctx.ProjectId);
            PathHelper.EnsureDirectory(root);
            var dumpPath = System.IO.Path.Combine(root, $"_architect_bad_json_{DateTime.Now:HHmmss}.txt");
            System.IO.File.WriteAllText(dumpPath,
                $"--- ERROR ---\n{ex.Message}\n\n" +
                $"--- ORIGINAL ---\n{original}\n\n" +
                $"--- EXTRACTED ---\n{extracted}\n\n" +
                $"--- SANITIZED ---\n{cleaned}\n");
        }
        catch { /* ignore */ }
    }
    private static void DetectCycles(List<Stage> stages)
    {
        var map = stages.ToDictionary(s => s.Id);
        var color = stages.ToDictionary(s => s.Id, _ => 0); // 0=white,1=gray,2=black
        void Dfs(string id)
        {
            color[id] = 1;
            foreach (var d in map[id].DependsOn)
            {
                if (color[d] == 1) throw new InvalidOperationException($"Цикл в этапах через {d}.");
                if (color[d] == 0) Dfs(d);
            }
            color[id] = 2;
        }
        foreach (var s in stages) if (color[s.Id] == 0) Dfs(s.Id);
    }

    /// <summary>Ищет первый корректно сбалансированный JSON-объект в тексте.</summary>
    private static string ExtractJson(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;

        // Срезаем markdown-обёртку ```json ... ``` или ``` ... ```
        var fence = System.Text.RegularExpressions.Regex.Match(
            s, @"```(?:json|jsonc)?\s*\r?\n(?<body>.*?)\r?\n```",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (fence.Success) s = fence.Groups["body"].Value;

        // Балансировщик — уважает строки и escape-последовательности.
        int start = s.IndexOf('{');
        if (start < 0) return s;

        int depth = 0;
        bool inString = false;
        bool escape = false;

        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return s.Substring(start, i - start + 1);
            }
        }
        return s[start..];
    }// не нашли пары — вернём как есть, дальше разберётся санитайзер}

    private static string RenderTzMarkdown(ProjectPlan p)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# ТЗ: {p.Title}\n");
        sb.AppendLine("## Краткое описание\n").AppendLine(p.Summary).AppendLine();

        sb.AppendLine("## Функциональные возможности\n");
        foreach (var f in p.Features)
            sb.AppendLine($"### {f.Name}\n- **Описание:** {f.Description}\n- **Преимущество:** {f.Advantage}\n");

        if (p.NonFunctionalRequirements.Count > 0)
        {
            sb.AppendLine("## Нефункциональные требования");
            foreach (var r in p.NonFunctionalRequirements) sb.AppendLine($"- {r}");
            sb.AppendLine();
        }

        if (p.Constraints.Count > 0)
        {
            sb.AppendLine("## Ограничения");
            foreach (var c in p.Constraints) sb.AppendLine($"- {c}");
            sb.AppendLine();
        }

        sb.AppendLine("## План этапов\n");
        foreach (var s in p.Stages)
        {
            sb.AppendLine($"### {s.Id}. {s.Name}");
            sb.AppendLine($"- **Цель:** {s.Goal}");
            sb.AppendLine($"- **Зависит от:** {(s.DependsOn.Count == 0 ? "—" : string.Join(", ", s.DependsOn))}");
            sb.AppendLine($"- **Область:** {string.Join(", ", s.Scope)}");
            sb.AppendLine("- **Результаты:**");
            foreach (var d in s.Deliverables) sb.AppendLine($"  - {d}");
            sb.AppendLine("- **Критерии приёмки:**");
            foreach (var a in s.AcceptanceCriteria) sb.AppendLine($"  - {a}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static async Task SaveTzAsync(AgentContext ctx, string tz, CancellationToken ct)
    {
        var root = ctx.OutputRoot
                ?? ctx.ProjectPath
                ?? PathHelper.GetProjectOutputRoot(ctx.ProjectId);
        PathHelper.EnsureDirectory(root);
        var path = Path.Combine(root, "TZ.md");
        await File.WriteAllTextAsync(path, tz, ct);
    }





    /// <summary>
    /// Приводит «полу-JSON» от LLM (qwen-coder, deepseek-coder и т.п.) к строгому JSON.
    /// </summary>
    private static string SanitizeJson(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;

        // 1) Удаляем однострочные комментарии // ... (вне строк)
        s = StripOutsideStrings(s, @"//[^\r\n]*");
        // 2) Удаляем многострочные /* ... */
        s = StripOutsideStrings(s, @"/\*.*?\*/", singleline: true);
        // 3) Одинарные кавычки строк → двойные (аккуратно, простая эвристика)
        s = ConvertSingleQuotedStrings(s);
        // 4) Кавычим неквотированные ключи:  { name: → { "name":
        s = QuoteUnquotedKeys(s);
        // 5) Убираем висящие запятые:  ,}   ,]   →   }   ]
        s = System.Text.RegularExpressions.Regex.Replace(s, @",(\s*[}\]])", "$1");

        return s;
    }

    // Удаляет матчи regex, но игнорирует то, что внутри "..." строк.
    private static string StripOutsideStrings(string s, string pattern, bool singleline = false)
    {
        var opts = System.Text.RegularExpressions.RegexOptions.None
                 | (singleline ? System.Text.RegularExpressions.RegexOptions.Singleline
                               : System.Text.RegularExpressions.RegexOptions.None);
        var sb = new System.Text.StringBuilder(s.Length);
        bool inStr = false; bool esc = false; int lastFlush = 0;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; continue; }
        }
        // Проще: маскируем строки, применяем regex, восстанавливаем.
        var strings = new System.Collections.Generic.List<string>();
        var masked = System.Text.RegularExpressions.Regex.Replace(
            s, "\"(?:\\\\.|[^\"\\\\])*\"",
            m => { strings.Add(m.Value); return $"\u0001{strings.Count - 1}\u0001"; });
        masked = System.Text.RegularExpressions.Regex.Replace(masked, pattern, "", opts);
        return System.Text.RegularExpressions.Regex.Replace(masked, @"\u0001(\d+)\u0001",
            m => strings[int.Parse(m.Groups[1].Value)]);
    }

    private static string QuoteUnquotedKeys(string s)
    {
        // Ключ = слово перед двоеточием, идущее после { или , (с пробелами/переводами строк)
        // Строки предварительно маскируем, чтобы не задеть.
        var strings = new System.Collections.Generic.List<string>();
        var masked = System.Text.RegularExpressions.Regex.Replace(
            s, "\"(?:\\\\.|[^\"\\\\])*\"",
            m => { strings.Add(m.Value); return $"\u0001{strings.Count - 1}\u0001"; });

        masked = System.Text.RegularExpressions.Regex.Replace(
            masked,
            @"(?<=[{,]\s*)(?<key>[A-Za-z_][A-Za-z0-9_\-]*)\s*:",
            m => $"\"{m.Groups["key"].Value}\":");

        return System.Text.RegularExpressions.Regex.Replace(masked, @"\u0001(\d+)\u0001",
            m => strings[int.Parse(m.Groups[1].Value)]);
    }

    private static string ConvertSingleQuotedStrings(string s)
    {
        // Заменяем 'text' → "text" только там, где это похоже на JSON-строку.
        // Пропускаем случаи, где апостроф внутри уже-двойной строки (маскируем).
        var strings = new System.Collections.Generic.List<string>();
        var masked = System.Text.RegularExpressions.Regex.Replace(
            s, "\"(?:\\\\.|[^\"\\\\])*\"",
            m => { strings.Add(m.Value); return $"\u0001{strings.Count - 1}\u0001"; });

        masked = System.Text.RegularExpressions.Regex.Replace(
            masked,
            @"'(?<v>(?:\\.|[^'\\])*)'",
            m => "\"" + m.Groups["v"].Value.Replace("\"", "\\\"") + "\"");

        return System.Text.RegularExpressions.Regex.Replace(masked, @"\u0001(\d+)\u0001",
            m => strings[int.Parse(m.Groups[1].Value)]);
    }
}