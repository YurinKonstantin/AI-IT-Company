using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Contracts;

public enum AgentRole
{
    Interpreter,
    Architect,
    BackendCoder,
    FrontendCoder,
    GameCoder,
    FullstackCoder,
    Tester,
    Builder,
    ErrorFixer,
    Secretary,
    Scaffolder,
    Translator,
    Documenter,
    Artist,
    Analyst,
    UxReviewer
}

public interface IAgent
{
    AgentRole Role { get; }
    string Name { get; }
    Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct);
}



public sealed record AgentResult(
    bool Success, string Output, string? Error = null,
    IReadOnlyDictionary<string, string>? Artifacts = null);

public enum ProjectType { Unknown, WinUI, Api, Console, MonogameGame, Maui, WindowsService }
public enum WorkMode { CreateNew, Improve, FixError, Document, Analyze, PlanArchitecture }