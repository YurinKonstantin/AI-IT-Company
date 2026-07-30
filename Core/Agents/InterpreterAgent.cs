using Core.Agents;
using Core.Configuration;
using Core.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents;

public sealed class InterpreterAgent : AgentBase
{
    public override AgentRole Role => AgentRole.Interpreter;
    protected override string DefaultSystemPrompt => """
        Ты — Интерпретатор. Верни СТРОГО JSON:
        {"mode":"CreateNew|Improve|FixError|Document|Analyze",
         "type":"WinUI|Api|Console|MonogameGame",
         "summary":"..."}
        Никакого другого текста.
    """;

    public InterpreterAgent(IAiProviderFactory factory, AgentConfigStore configStore, AgentPromptStore promptStore,
                        ILogger<InterpreterAgent> logger) : base(factory, configStore, promptStore, logger) { }

    protected override string BuildUserPrompt(AgentContext ctx)
        => $"Промпт пользователя: {ctx.UserPrompt}\nПуть к проекту: {ctx.ProjectPath ?? "нет"}";

    protected override Task<AgentResult> PostProcessAsync(AgentContext ctx, string output, CancellationToken ct)
    {
        try
        {
            var json = ExtractJson(output);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            ctx.Mode = Enum.Parse<WorkMode>(root.GetProperty("mode").GetString()!);
            ctx.Type = Enum.Parse<ProjectType>(root.GetProperty("type").GetString()!);
            ctx.SharedData["summary"] = root.GetProperty("summary").GetString() ?? "";
            return Task.FromResult(new AgentResult(true, json));
        }
        catch (Exception ex) { return Task.FromResult(new AgentResult(false, output, ex.Message)); }
    }

    private static string ExtractJson(string s)
    {
        int a = s.IndexOf('{'), b = s.LastIndexOf('}');
        return (a >= 0 && b > a) ? s[a..(b + 1)] : s;
    }
}