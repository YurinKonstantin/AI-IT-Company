
using Core.Agents;
using Core.Configuration;
using Core.Contracts;
using Core.Models;
using Core.Services;
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
    ITranslationService _translator;
    protected override string DefaultSystemPrompt => """
    You are the Architect. Your task is to create a TECHNICAL SPECIFICATION and STAGE PLAN.
    IT IS STRICTLY PROHIBITED TO:
    - Write code (no C#, XAML, JSON application code, csproj, etc.).
    - Specify concrete file names, classes, methods, or variables.
    - Specify concrete libraries or NuGet packages (that is the Coder's decision).

    WHAT YOU MUST DO:
    1. Understand the user's task.
    2. Formulate functional capabilities with descriptions and user benefits.
    3. Compose non-functional requirements (performance, reliability, UX, etc.).
    4. Break down the work into STAGES such that:
       - each stage results in a WORKING program (even with limited functionality),
       - explicitly indicate which other stages it depends on,
       - include acceptance criteria to verify success.

    STRICTLY: all keys and string values MUST be in DOUBLE QUOTES. No //-comments,
    no trailing commas, no single quotes. Response must be exactly one JSON object,
    without markdown wrapper.

    RESPONSE FORMAT — STRICTLY JSON (no markdown wrappers, no explanations):
    {
      "title": "...",
      "summary": "2–4 sentences about the project",
      "features": [
        {"name":"...","description":"...","advantage":"user benefit"}
      ],
      "nonFunctionalRequirements": ["..."],
      "constraints": ["..."],
      "stages": [
        {
          "id": "S1",
          "name": "Minimum Viable Version",
          "goal": "What should work after this stage",
          "deliverables": ["functional items, WITHOUT mentioning files/classes"],
          "acceptanceCriteria": ["..."],
          "dependsOn": [],
          "scope": ["Backend","Frontend"]
        }
      ]
    }

    STAGE RULES:
    - S1 is always independent (dependsOn: []) and provides a minimal working result.
    - Each subsequent stage should only depend on previous ones where truly necessary.
    - Try to make stages as independent as possible — this is important for fault tolerance.
    - scope accepts values: "Backend", "Frontend", "Game", "Tests", "Docs".

    DOCUMENTATION MODE (Mode=Document):
    - Do NOT plan coding, scaffolding, tests, or build work.
    - Produce 1–3 stages with scope ONLY ["Docs"].
    - Focus on structure analysis, README, architecture notes, API docs.

    IMPROVE MODE (Mode=Improve):
    - The project ALREADY EXISTS. Do NOT plan greenfield scaffolding.
    - Plan ONLY the delta requested by the user (enhancements/refactors).
    - Prefer fewer stages (1–4). Reuse existing modules; avoid rewriting the whole app.
    - Mention in summary what stays unchanged.

    PLAN ARCHITECTURE MODE (Mode=PlanArchitecture):
    - Produce a clear TZ and stage plan only. Prefer 2–5 stages.
    - Do NOT assume code will be written in this run.
    - For WindowsService: scope mainly ["Backend"]; cover Worker/BackgroundService, logging, install notes.
    - For Maui: scope ["Backend","Frontend"] as appropriate for cross-platform UI.
    - For MonogameGame: include ["Game"] stages.
""";

    public ArchitectAgent(IAiProviderFactory factory, AgentConfigStore configStore, AgentPromptStore promptStore, ITranslationService translator,
                          ILogger<ArchitectAgent> logger)
        : base(factory, configStore, promptStore, translator, logger) { _translator = translator; }

    protected override string BuildUserPrompt(AgentContext ctx)
    {
        var files = ctx.Files.Count > 0 ? string.Join("\n", ctx.Files.Take(80)) : "(new project)";
        var modeHint = ctx.Mode switch
        {
            WorkMode.Document => """
              IMPORTANT: Mode is Document. Stages must use scope ["Docs"] only.
              Do not include Backend, Frontend, Game, or Tests scopes.
              """,
            WorkMode.Improve => """
              IMPORTANT: Mode is Improve. Plan ONLY changes to the existing codebase.
              Do not assume a new empty project. Prefer small incremental stages.
              Use structure/metrics below as ground truth about what already exists.
              """,
            WorkMode.PlanArchitecture => """
              IMPORTANT: Mode is PlanArchitecture. Deliver TZ + staged plan only.
              No coding will happen in this run. Keep stages conceptual and testable later.
              """,
            _ => ""
        };

        var typeHint = ctx.Type switch
        {
            ProjectType.WindowsService => """
              Project type is WindowsService: Worker / IHostedService, no UI.
              Prefer scope ["Backend"] (and ["Tests"] if useful). Mention install/uninstall as docs constraints, not code file names.
              """,
            ProjectType.Maui => """
              Project type is .NET MAUI: cross-platform UI. Prefer Backend+Frontend scopes; avoid WinUI-specific assumptions.
              """,
            ProjectType.MonogameGame => """
              Project type is MonoGame: include Game scope; assets may be produced by Artist before GameCoder.
              """,
            ProjectType.Api => """
              Project type is ASP.NET Core Web API: Backend (+ Tests). No desktop UI stages.
              """,
            _ => ""
        };

        var structure = Truncate(ctx.SharedData.GetValueOrDefault("project_structure", ""), 4000);
        var metrics = Truncate(ctx.SharedData.GetValueOrDefault("project_metrics", ""), 2500);

        return $"""
            User task: {ctx.UserPrompt}
            Project type: {ctx.Type}
            Mode: {ctx.Mode}
            {modeHint}
            {typeHint}

            Existing files:
            {files}

            Project structure:
            {structure}

            Project metrics:
            {metrics}

            Create a technical specification and a phased plan in the required JSON format.
            """;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "(none)" : s.Length <= max ? s : s[..max] + "\n…";

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




            // Переводим ТЗ обратно на язык пользователя для отображения.
            var tzTranslated = await _translator.ToUserAsync(tzMarkdown, ct);
            ctx.SharedData["tz_markdown_original"] = tzMarkdown;   // оставим оригинал для кодеров
            ctx.SharedData["tz_markdown_localized"] = tzTranslated;

            // Файл сохраняем в двух версиях:
            await SaveTzAsync(ctx, tzMarkdown, ct);                                 // TZ.md — рабочий
            await SaveTzLocalizedAsync(ctx, tzTranslated, ct);                      // TZ.localized.md


            return new AgentResult(true, tzTranslated);                             // пользователю — локализованный
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Architect] Не удалось разобрать план: {Msg}", ex.Message);
            return new AgentResult(false, output, ex.Message);
        }
    }
    private static async Task SaveTzLocalizedAsync(AgentContext ctx, string text, CancellationToken ct)
    {
        var root = ctx.OutputRoot ?? ctx.ProjectPath ?? PathHelper.GetProjectOutputRoot(ctx.ProjectId);
        PathHelper.EnsureDirectory(root);
        var path = Path.Combine(root, "TZ.localized.md");
        await File.WriteAllTextAsync(path, text, ct);
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
        sb.AppendLine($"# Technical Specifications: {p.Title}\n");
        sb.AppendLine("## Brief description\n").AppendLine(p.Summary).AppendLine();

        sb.AppendLine("## Functional capabilities\n");
        foreach (var f in p.Features)
            sb.AppendLine($"### {f.Name}\n- **Description:** {f.Description}\n- **Advantage:** {f.Advantage}\n");

        if (p.NonFunctionalRequirements.Count > 0)
        {
            sb.AppendLine("## Non-functional requirements");
            foreach (var r in p.NonFunctionalRequirements) sb.AppendLine($"- {r}");
            sb.AppendLine();
        }

        if (p.Constraints.Count > 0)
        {
            sb.AppendLine("## Restrictions");
            foreach (var c in p.Constraints) sb.AppendLine($"- {c}");
            sb.AppendLine();
        }

        sb.AppendLine("## Phased plan\n");
        foreach (var s in p.Stages)
        {
            sb.AppendLine($"### {s.Id}. {s.Name}");
            sb.AppendLine($"- **Target:** {s.Goal}");
            sb.AppendLine($"- **It depends on:** {(s.DependsOn.Count == 0 ? "—" : string.Join(", ", s.DependsOn))}");
            sb.AppendLine($"- **Region:** {string.Join(", ", s.Scope)}");
            sb.AppendLine("- **Результаты:**");
            foreach (var d in s.Deliverables) sb.AppendLine($"  - {d}");
            sb.AppendLine("- **Acceptance criteria:**");
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