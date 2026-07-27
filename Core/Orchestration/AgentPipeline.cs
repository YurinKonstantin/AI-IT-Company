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

    public async Task RunAsync(AgentContext ctx, CancellationToken ct)
    {
        // 1) Интерпретация
        await RunOneAsync(AgentRole.Interpreter, ctx, ct);

        // 2) Архитектура
        await RunOneAsync(AgentRole.Architect, ctx, ct);

        // 3) Кодеры — параллельно в зависимости от типа
        var coders = new List<Task>();
        if (ctx.Type is ProjectType.Api or ProjectType.Console or ProjectType.WinUI)
            coders.Add(RunOneAsync(AgentRole.BackendCoder, ctx, ct));
        if (ctx.Type == ProjectType.WinUI)
            coders.Add(RunOneAsync(AgentRole.FrontendCoder, ctx, ct));
        if (ctx.Type == ProjectType.MonogameGame)
            coders.Add(RunOneAsync(AgentRole.GameCoder, ctx, ct));
        await Task.WhenAll(coders);

        // 4) Тесты
        if (ctx.Mode != WorkMode.Document)
            await RunOneAsync(AgentRole.Tester, ctx, ct);

        // 5) Сборка + цикл исправлений
        await RunOneAsync(AgentRole.Builder, ctx, ct);
        int attempts = 0;
        while (ctx.SharedData.GetValueOrDefault("build_ok") == "false" && attempts++ < 5)
        {
            ctx.SharedData.Remove("fix_code");
            await RunOneAsync(AgentRole.ErrorFixer, ctx, ct);
            await RunOneAsync(AgentRole.Builder, ctx, ct);
        }

        // 6) Отчёт
        await RunOneAsync(AgentRole.Secretary, ctx, ct);
        Events.Writer.TryComplete();
    }

    private async Task RunOneAsync(AgentRole role, AgentContext ctx, CancellationToken ct)
    {
        if (!_agents.TryGetValue(role, out var agent)) return;
        await Events.Writer.WriteAsync(new(role, "start", "", DateTime.UtcNow), ct);
        var result = await agent.ExecuteAsync(ctx, ct);
        await Events.Writer.WriteAsync(
            new(role, result.Success ? "done" : "error",
                result.Success ? result.Output : result.Error ?? "", DateTime.UtcNow), ct);
    }
}