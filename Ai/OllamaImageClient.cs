using Core.Contracts;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ai;

/// <summary>
/// Генерация изображений через Ollama (/api/generate), если модель умеет image output.
/// При неудаче Artist откатывается на процедурный генератор.
/// </summary>
public sealed class OllamaImageClient : IImageGenClient, IDisposable
{
    private readonly Func<string> _baseUrl;
    private HttpClient _http;
    private readonly object _lock = new();

    public ArtistImageMode Mode => ArtistImageMode.Ollama;
    public string Name => "Ollama";

    public OllamaImageClient(Func<string> baseUrl)
    {
        _baseUrl = baseUrl;
        _http = Create(Normalize(baseUrl()));
    }

    public void RefreshBaseUrl()
    {
        var url = Normalize(_baseUrl());
        lock (_lock)
        {
            var current = _http.BaseAddress?.ToString().TrimEnd('/') ?? "";
            if (string.Equals(current, url, StringComparison.OrdinalIgnoreCase)) return;
            _http.Dispose();
            _http = Create(url);
        }
    }

    public async Task<ImageGenResult> GeneratePngAsync(ImageGenRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
            return new ImageGenResult(false, null,
                "Не задана Ollama image-модель (Настройки → Artist image-gen).");

        RefreshBaseUrl();
        var prompt = $"""
            {request.StyleHint}. Game sprite, transparent background preferred.
            Kind: {request.Kind}. {request.Prompt}. Size ~{request.Width}x{request.Height}.
            Single centered subject, no text.
            """;

        try
        {
            var payload = new
            {
                model = request.Model.Trim(),
                prompt,
                stream = false,
                // Некоторые модели принимают format/options
                options = new { temperature = 0.7 }
            };

            using var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/generate", content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new ImageGenResult(false, null, $"Ollama {(int)resp.StatusCode}: {Trim(body)}");

            var bytes = ExtractImage(body);
            return bytes is null
                ? new ImageGenResult(false, null,
                    "Ollama ответила без изображения. Нужна image-capable модель (см. ollama.com).")
                : new ImageGenResult(true, bytes);
        }
        catch (Exception ex)
        {
            return new ImageGenResult(false, null, "Ollama image: " + ex.Message);
        }
    }

    private static byte[]? ExtractImage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // image / images[]
            if (root.TryGetProperty("image", out var one) && one.ValueKind == JsonValueKind.String)
                return Decode(one.GetString());

            if (root.TryGetProperty("images", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var b = Decode(item.GetString());
                        if (b is not null) return b;
                    }
                }
            }

            // иногда в response лежит data URL
            if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.String)
            {
                var text = response.GetString() ?? "";
                var idx = text.IndexOf("data:image/", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var end = idx;
                    while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
                    var dataUrl = text[idx..end];
                    var marker = "base64,";
                    var m = dataUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (m >= 0) return Decode(dataUrl[(m + marker.Length)..]);
                }
            }
        }
        catch { /* ignore */ }

        // Переиспользуем парсер OpenRouter на случай совместимого JSON.
        return OpenRouterImageClient.ExtractBase64Image(json);
    }

    private static byte[]? Decode(string? b64)
    {
        if (string.IsNullOrWhiteSpace(b64)) return null;
        try
        {
            return Convert.FromBase64String(b64.Trim().Replace("\n", "").Replace("\r", ""));
        }
        catch { return null; }
    }

    private static HttpClient Create(string baseUrl) => new()
    {
        BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"),
        Timeout = TimeSpan.FromMinutes(5)
    };

    private static string Normalize(string url)
        => string.IsNullOrWhiteSpace(url) ? "http://localhost:11434" : url.Trim().TrimEnd('/');

    private static string Trim(string s) => s.Length <= 400 ? s : s[..400] + "…";

    public void Dispose() => _http.Dispose();
}
