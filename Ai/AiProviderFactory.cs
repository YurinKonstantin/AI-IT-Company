using Core.Contracts;
using Microsoft.Extensions.Logging;
using System;

namespace Ai;

public sealed class AiProviderFactory : IAiProviderFactory
{
    private readonly OllamaProvider _ollama;
    private readonly OpenRouterProvider _openRouter;
    private readonly OnnxProvider _onnx;
    private readonly ILogger<AiProviderFactory> _logger;

    public AiProviderFactory(
        OllamaProvider ollama,
        OpenRouterProvider openRouter,
        OnnxProvider onnx,
        ILogger<AiProviderFactory> logger)
    {
        _ollama = ollama;
        _openRouter = openRouter;
        _onnx = onnx;
        _logger = logger;
    }

    public IAiProvider Resolve(string source) => source?.Trim().ToLowerInvariant() switch
    {
        "ollama" => _ollama,
        "openrouter" => _openRouter,
        "onnx" or "microsoft" => _onnx,
        _ => FallbackToOllama(source)
    };

    private IAiProvider FallbackToOllama(string? src)
    {
        _logger.LogWarning("Источник '{Source}' не поддерживается — используется Ollama (локальный приоритет).", src);
        return _ollama;
    }
}
