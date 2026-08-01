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
    You are an Interpreter. Return STRICTLY JSON:
    {"mode":"CreateNew|Improve|FixError|Document|Analyze",
     "type":"WinUI|Api|Console|MonogameGame",
     "summary":"..."}
    No other text.
    If the user already selected a work mode in the UI, still return your best-guess mode
    in the JSON — the orchestrator may ignore it and keep the UI choice.
    """;

    public InterpreterAgent(IAiProviderFactory factory, AgentConfigStore configStore, AgentPromptStore promptStore,
                        ILogger<InterpreterAgent> logger) : base(factory, configStore, promptStore, logger) { }

    protected override string BuildUserPrompt(AgentContext ctx)
        => $"""
            User prompt: {ctx.UserPrompt}
            Project path: {ctx.ProjectPath ?? "нет"}
            UI-selected mode (authoritative if locked): {ctx.Mode}
            ModeLocked: {ctx.ModeLocked}
            Already detected type: {ctx.Type}
            """;

    protected override Task<AgentResult> PostProcessAsync(AgentContext ctx, string output, CancellationToken ct)
    {
        try
        {
            var json = ExtractJson(output);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var suggestedMode = Enum.Parse<WorkMode>(root.GetProperty("mode").GetString()!);
            var suggestedType = Enum.Parse<ProjectType>(root.GetProperty("type").GetString()!);

            ctx.SharedData["interpreter_suggested_mode"] = suggestedMode.ToString();
            ctx.SharedData["interpreter_suggested_type"] = suggestedType.ToString();

            // Режим из UI имеет приоритет.
            if (!ctx.ModeLocked)
                ctx.Mode = suggestedMode;

            // Тип из сканера проекта важнее догадки модели.
            if (ctx.Type == ProjectType.Unknown)
                ctx.Type = suggestedType;

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
