
using Core.Agents;
using Core.Configuration;
using Core.Contracts;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents;

public sealed class ArchitectAgent : AgentBase
{
    public override AgentRole Role => AgentRole.Architect;
    protected override string SystemPrompt => """
        Ты — Архитектор. Для запросов создания опиши структуру:
        - список файлов с относительными путями,
        - краткое описание каждого,
        - зависимости NuGet.
        Для анализа существующих — выведи диагноз и план изменений.
        Формат: Markdown со списками.
    """;

    public ArchitectAgent(IAiProviderFactory factory, AgentConfigStore configStore,
                        ILogger<InterpreterAgent> logger) : base(factory, configStore, logger) { }

    protected override string BuildUserPrompt(AgentContext ctx)
    {
        var mode = ctx.Mode.ToString();
        var files = ctx.Files.Count > 0 ? string.Join("\n", ctx.Files) : "(новый проект)";
        return $"Режим: {mode}\nТип проекта: {ctx.Type}\nЗадача: {ctx.UserPrompt}\nСуществующие файлы:\n{files}";
    }

    protected override Task<AgentResult> PostProcessAsync(AgentContext ctx, string output, CancellationToken ct)
    {
        ctx.SharedData["architecture"] = output;
        return Task.FromResult(new AgentResult(true, output));
    }
}