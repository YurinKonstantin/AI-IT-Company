using Core.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Core.Orchestration;

public sealed record PipelineEvent(AgentRole Role, string Kind, string Payload, DateTime At);

/// <summary>
/// Устаревший монолитный пайплайн. Не регистрируется в DI.
/// Используйте <see cref="StagedPipeline"/> — он поддерживает режимы
/// CreateNew / Improve / FixError / Document / Analyze.
/// </summary>
[Obsolete("Use StagedPipeline instead. AgentPipeline is kept only for reference and is not DI-registered.")]
public sealed class AgentPipeline
{
    private readonly Dictionary<AgentRole, IAgent> _agents;
    private readonly ILogger<AgentPipeline> _logger;
    public Channel<PipelineEvent> Events { get; } =
        Channel.CreateUnbounded<PipelineEvent>(new UnboundedChannelOptions { SingleReader = false });

    public AgentPipeline(IEnumerable<IAgent> agents, ILogger<AgentPipeline> logger)
    {
        _agents = agents.ToDictionary(a => a.Role);
        _logger = logger;
    }

    public Task RunAsync(AgentContext ctx, CancellationToken ct)
    {
        _logger.LogWarning(
            "AgentPipeline устарел и не должен вызываться. Используйте StagedPipeline. Mode={Mode}",
            ctx.Mode);
        Events.Writer.TryComplete();
        return Task.FromException(new InvalidOperationException(
            "AgentPipeline is obsolete. Use StagedPipeline."));
    }
}
