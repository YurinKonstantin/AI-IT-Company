
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


    public IAiProvider Resolve(string source)
    {
        if (!string.Equals(source, "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Источник '{Source}' временно не поддерживается — используется Ollama.",
                source);
        }
        return _ollama;
    }
}