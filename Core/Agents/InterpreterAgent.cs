using Core.Configuration;
using Core.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents;

public sealed class InterpreterAgent : AgentBase
{
    public override AgentRole Role => AgentRole.Interpreter;
    protected override string DefaultSystemPrompt => """
    You are an Interpreter. Classify the user's request, then return STRICTLY JSON:
    {
      "intent":"CreateApp|CreateGame|CreateService|Improve|FixError|Document|Analyze|PlanArchitecture|Unknown",
      "mode":"CreateNew|Improve|FixError|Document|Analyze|PlanArchitecture",
      "type":"WinUI|Api|Console|MonogameGame|Maui|WindowsService|Unknown",
      "summary":"one short sentence",
      "needsExistingProject":false,
      "confidence":0.0
    }
    No other text.

    Intent guide:
    - CreateApp: new desktop/web/mobile app (WinUI, Api, Console, Maui)
    - CreateGame: new MonoGame / game
    - CreateService: Windows Service / background Worker
    - Improve: enhance existing codebase
    - FixError: fix build/runtime bugs in existing project
    - Document: write docs only
    - Analyze: review and propose improvements (no coding)
    - PlanArchitecture: only TZ / architecture plan, no coding/scaffold/build

    Mode mapping:
    - CreateApp/CreateGame/CreateService → CreateNew
    - Improve → Improve; FixError → FixError; Document → Document; Analyze → Analyze
    - PlanArchitecture → PlanArchitecture

    Type hints: game→MonogameGame; Windows service/worker→WindowsService; MAUI/mobile→Maui;
    WinUI/desktop Windows UI→WinUI; REST/API→Api; console CLI→Console.

    needsExistingProject=true for Improve, FixError, Document, Analyze.
    confidence is 0..1. If unsure, lower confidence and use Unknown type when needed.
    If the user already selected a work mode in the UI, still return your best-guess —
    the orchestrator may ignore mode when ModeLocked=true.
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

            var intent = GetString(root, "intent") ?? "Unknown";
            var suggestedMode = ParseMode(GetString(root, "mode"), intent);
            var suggestedType = ParseType(GetString(root, "type"), intent);
            var summary = GetString(root, "summary") ?? "";
            var needsExisting = GetBool(root, "needsExistingProject")
                ?? suggestedMode is WorkMode.Improve or WorkMode.FixError
                    or WorkMode.Document or WorkMode.Analyze;
            var confidence = GetDouble(root, "confidence") ?? 0.7;

            ctx.SharedData["interpreter_intent"] = intent;
            ctx.SharedData["interpreter_suggested_mode"] = suggestedMode.ToString();
            ctx.SharedData["interpreter_suggested_type"] = suggestedType.ToString();
            ctx.SharedData["interpreter_needs_existing"] = needsExisting.ToString().ToLowerInvariant();
            ctx.SharedData["interpreter_confidence"] = confidence.ToString("0.###", CultureInfo.InvariantCulture);
            ctx.SharedData["summary"] = summary;

            if (!ctx.ModeLocked)
            {
                if (confidence < 0.5)
                {
                    ctx.Mode = WorkMode.CreateNew;
                    ctx.SharedData["interpreter_low_confidence"] = "true";
                    if (ctx.Type == ProjectType.Unknown)
                        ctx.Type = ProjectType.Unknown;
                }
                else
                {
                    ctx.Mode = suggestedMode;
                    if (ctx.Type == ProjectType.Unknown && suggestedType != ProjectType.Unknown)
                        ctx.Type = suggestedType;
                }
            }
            else if (ctx.Type == ProjectType.Unknown && suggestedType != ProjectType.Unknown)
            {
                ctx.Type = suggestedType;
            }

            return Task.FromResult(new AgentResult(true, json));
        }
        catch (Exception ex) { return Task.FromResult(new AgentResult(false, output, ex.Message)); }
    }

    private static WorkMode ParseMode(string? mode, string intent)
    {
        if (!string.IsNullOrWhiteSpace(mode)
            && Enum.TryParse<WorkMode>(mode, ignoreCase: true, out var parsed))
            return parsed;

        return intent?.Trim() switch
        {
            "Improve" => WorkMode.Improve,
            "FixError" => WorkMode.FixError,
            "Document" => WorkMode.Document,
            "Analyze" => WorkMode.Analyze,
            "PlanArchitecture" => WorkMode.PlanArchitecture,
            _ => WorkMode.CreateNew
        };
    }

    private static ProjectType ParseType(string? type, string intent)
    {
        if (!string.IsNullOrWhiteSpace(type) && Enum.TryParse<ProjectType>(type, ignoreCase: true, out var t))
            return t;

        return intent?.Trim() switch
        {
            "CreateGame" => ProjectType.MonogameGame,
            "CreateService" => ProjectType.WindowsService,
            _ => ProjectType.Unknown
        };
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool? GetBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(p.GetString(), out var b) => b,
            _ => null
        };
    }

    private static double? GetDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d)) return d;
        if (p.ValueKind == JsonValueKind.String
            && double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            return s;
        return null;
    }

    private static string ExtractJson(string s)
    {
        int a = s.IndexOf('{'), b = s.LastIndexOf('}');
        return (a >= 0 && b > a) ? s[a..(b + 1)] : s;
    }
}
