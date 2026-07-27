using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Contracts;

public interface IAiProvider
{
    string Name { get; }
    IAsyncEnumerable<string> GenerateStreamAsync(
        AiRequest request, CancellationToken ct = default);
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

public record AiRequest(
    string ModelName,
    string SystemPrompt,
    string UserPrompt,
    double Temperature = 0.7,
    int MaxTokens = 0,          // 0 = безлимит
    int ContextWindow = 128_000,
    TimeSpan? Timeout = null);   // null = бесконечно