
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
            var json = ExtractJson(output);
            var plan = JsonSerializer.Deserialize<ProjectPlan>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Пустой JSON от архитектора.");

            ValidatePlan(plan);

            // Сохраняем в SharedData как объект и как исходный JSON.
            ctx.SharedData["project_plan_json"] = json;
            ctx.Plan = plan;

            // Пишем ТЗ в человекочитаемом виде.
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

    private static string ExtractJson(string s)
    {
        int a = s.IndexOf('{'), b = s.LastIndexOf('}');
        return (a >= 0 && b > a) ? s[a..(b + 1)] : s;
    }

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
}