using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ai
{
    // Абстракция инференса — из Microsoft.Extensions.AI (готовая, не пишем сами).
    // IChatClient  +  IChatClient.GetStreamingResponseAsync(...)

    // Наша абстракция управления моделями провайдера
    public interface IModelCatalog
    {
        string ProviderName { get; }
        Task<bool> IsAvailableAsync(CancellationToken ct = default);
        Task<IReadOnlyList<AiModelInfo>> ListAsync(CancellationToken ct = default);
        IAsyncEnumerable<ModelPullProgress> PullAsync(string modelName, CancellationToken ct = default);
        Task<bool> DeleteAsync(string modelName, CancellationToken ct = default);
    }

    public sealed record AiModelInfo(
        string Name,
        long SizeBytes,
        DateTime ModifiedAt,
        string? Family = null,
        string? ParameterSize = null,
        string? Quantization = null);

    public sealed record ModelPullProgress(
        string Status,
        long? Total,
        long? Completed,
        string? Error);

    public enum AiProviderKind { Ollama, OnnxDirectML, PhiSilica, OpenAI }

    // Фабрика возвращает готовый IChatClient и связанный с ним каталог моделей.
    public interface IAiClientFactory
    {
        IChatClient CreateChatClient(AiProviderKind kind, AiClientOptions options);
        IModelCatalog CreateCatalog(AiProviderKind kind, AiClientOptions options);
    }

    public sealed class AiClientOptions
    {
        public string BaseUrl { get; init; } = "http://localhost:11434";
        public string ModelName { get; init; } = "";
        public string? ApiKey { get; init; }
        public float Temperature { get; init; } = 0.7f;
        public int? MaxOutputTokens { get; init; }
        public int? ContextWindow { get; init; }
    }
}
