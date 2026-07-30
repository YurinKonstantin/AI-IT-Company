
using Core.Contracts;
using Microsoft.Extensions.Logging;
using System;

namespace Ai;

public sealed class AiProviderFactory : IAiProviderFactory
{
    private readonly OllamaProvider _ollama;
    //  private readonly OnnxProvider _onnx;
    private readonly ILogger<AiProviderFactory> _logger;

    public AiProviderFactory(OllamaProvider ollama, ILogger<AiProviderFactory> logger)
    {
        _ollama = ollama;
        _logger = logger;
    }


    public IAiProvider Resolve(string source) => source?.ToLowerInvariant() switch
    {
        "ollama" => _ollama,
        // "onnx"      => _onnx,       // включим на Шаге 2
        // "phisilica" => _phiSilica,  // включим на Шаге 3
        _ => FallbackToOllama(source)
    };
    private IAiProvider FallbackToOllama(string? src)
    {
        _logger.LogWarning("Источник '{Source}' пока не поддерживается — используется Ollama.", src);
        return _ollama;
    }
}