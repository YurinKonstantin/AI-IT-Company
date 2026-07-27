using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Contracts;

public enum AgentRole
{
    Interpreter, Architect,
    BackendCoder, FrontendCoder, GameCoder,
    Tester, Builder, ErrorFixer, Secretary
}

public interface IAgent
{
    AgentRole Role { get; }
    string Name { get; }
    Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct);
}

public sealed class AgentContext
{
    public required string ProjectId { get; init; }
    public required string UserPrompt { get; init; }
    public string? ProjectPath { get; set; }
    public ProjectType Type { get; set; } = ProjectType.Unknown;
    public WorkMode Mode { get; set; } = WorkMode.CreateNew;
    public Dictionary<string, string> SharedData { get; } = new();
    public List<string> Files { get; } = new();
}

public sealed record AgentResult(
    bool Success, string Output, string? Error = null,
    IReadOnlyDictionary<string, string>? Artifacts = null);

public enum ProjectType { Unknown, WinUI, Api, Console, MonogameGame }
public enum WorkMode { CreateNew, Improve, FixError, Document, Analyze }