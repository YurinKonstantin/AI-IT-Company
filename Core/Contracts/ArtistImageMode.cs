using System.Threading;
using System.Threading.Tasks;

namespace Core.Contracts;

/// <summary>Источник пикселей для ArtistAgent.</summary>
public enum ArtistImageMode
{
    /// <summary>Локальный процедурный pixel-art (по умолчанию).</summary>
    Procedural = 0,

    /// <summary>OpenRouter image API (нужен API key).</summary>
    OpenRouter = 1,

    /// <summary>Ollama image-capable модель (локально).</summary>
    Ollama = 2
}

public sealed record ImageGenRequest(
    string Prompt,
    int Width,
    int Height,
    string? Model,
    string StyleHint,
    string AssetId,
    string Kind);

public sealed record ImageGenResult(bool Success, byte[]? PngBytes, string? Error = null);

/// <summary>Удалённый/локальный image-gen клиент (не процедурный).</summary>
public interface IImageGenClient
{
    ArtistImageMode Mode { get; }
    string Name { get; }
    Task<ImageGenResult> GeneratePngAsync(ImageGenRequest request, CancellationToken ct = default);
}
