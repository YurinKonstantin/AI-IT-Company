using Core.Contracts;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ai;

/// <summary>
/// Генерация изображений через OpenRouter:
/// 1) POST /images (предпочтительно)
/// 2) fallback: POST /chat/completions + modalities
/// </summary>
public sealed class OpenRouterImageClient : IImageGenClient, IDisposable
{
    private readonly WindowsCredentialStore _credentials;
    private readonly Func<string> _baseUrl;
    private HttpClient _http;
    private readonly object _lock = new();

    public ArtistImageMode Mode => ArtistImageMode.OpenRouter;
    public string Name => "OpenRouter";

    public OpenRouterImageClient(WindowsCredentialStore credentials, Func<string> baseUrl)
    {
        _credentials = credentials;
        _baseUrl = baseUrl;
        _http = Create(Normalize(baseUrl()));
    }

    public void RefreshBaseUrl()
    {
        var url = Normalize(_baseUrl());
        lock (_lock)
        {
            if (_http.BaseAddress?.ToString().TrimEnd('/') == url.TrimEnd('/')) return;
            _http.Dispose();
            _http = Create(url);
        }
    }

    public async Task<ImageGenResult> GeneratePngAsync(ImageGenRequest request, CancellationToken ct = default)
    {
        var key = _credentials.GetSecret(WindowsCredentialStore.OpenRouterResource);
        if (string.IsNullOrWhiteSpace(key))
            return new ImageGenResult(false, null, "OpenRouter API key не задан.");

        RefreshBaseUrl();
        var model = string.IsNullOrWhiteSpace(request.Model)
            ? "google/gemini-2.5-flash-image-preview"
            : request.Model.Trim();

        var prompt = BuildPrompt(request);

        // 1) Dedicated /images
        var viaImages = await TryImagesEndpointAsync(key, model, prompt, request, ct);
        if (viaImages.Success) return viaImages;

        // 2) Chat completions + modalities
        var viaChat = await TryChatModalitiesAsync(key, model, prompt, ct);
        if (viaChat.Success) return viaChat;

        return new ImageGenResult(false, null,
            viaChat.Error ?? viaImages.Error ?? "OpenRouter не вернул изображение.");
    }

    private async Task<ImageGenResult> TryImagesEndpointAsync(
        string key, string model, string prompt, ImageGenRequest request, CancellationToken ct)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["prompt"] = prompt,
                ["n"] = 1,
                ["output_format"] = "png"
            };

            // Подсказка размера, если API поддерживает.
            if (request.Width > 0 && request.Height > 0)
                payload["size"] = $"{request.Width}x{request.Height}";

            using var req = new HttpRequestMessage(HttpMethod.Post, "images")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            ApplyAuth(req, key);

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new ImageGenResult(false, null, $"POST /images {(int)resp.StatusCode}: {Trim(body)}");

            var bytes = ExtractBase64Image(body);
            return bytes is null
                ? new ImageGenResult(false, null, "POST /images: нет b64 в ответе")
                : new ImageGenResult(true, bytes);
        }
        catch (Exception ex)
        {
            return new ImageGenResult(false, null, "POST /images: " + ex.Message);
        }
    }

    private async Task<ImageGenResult> TryChatModalitiesAsync(
        string key, string model, string prompt, CancellationToken ct)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["stream"] = false,
                ["modalities"] = new[] { "image", "text" },
                ["messages"] = new object[]
                {
                    new
                    {
                        role = "user",
                        content = prompt
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            ApplyAuth(req, key);

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new ImageGenResult(false, null, $"POST /chat/completions {(int)resp.StatusCode}: {Trim(body)}");

            var bytes = ExtractBase64Image(body);
            return bytes is null
                ? new ImageGenResult(false, null, "chat/completions: нет image в ответе")
                : new ImageGenResult(true, bytes);
        }
        catch (Exception ex)
        {
            return new ImageGenResult(false, null, "chat/completions: " + ex.Message);
        }
    }

    internal static byte[]? ExtractBase64Image(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // /images → data[].b64_json
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String)
                        return Decode(b64.GetString());
                    if (item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                    {
                        var decoded = DecodeDataUrl(url.GetString());
                        if (decoded is not null) return decoded;
                    }
                }
            }

            // chat → choices[0].message.images[].image_url.url
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var msg = choices[0].GetProperty("message");
                if (msg.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
                {
                    foreach (var img in images.EnumerateArray())
                    {
                        if (img.TryGetProperty("image_url", out var iu))
                        {
                            if (iu.ValueKind == JsonValueKind.Object
                                && iu.TryGetProperty("url", out var u))
                            {
                                var decoded = DecodeDataUrl(u.GetString());
                                if (decoded is not null) return decoded;
                            }
                            else if (iu.ValueKind == JsonValueKind.String)
                            {
                                var decoded = DecodeDataUrl(iu.GetString());
                                if (decoded is not null) return decoded;
                            }
                        }
                        if (img.TryGetProperty("url", out var direct))
                        {
                            var decoded = DecodeDataUrl(direct.GetString());
                            if (decoded is not null) return decoded;
                        }
                        if (img.TryGetProperty("b64_json", out var b64))
                        {
                            var decoded = Decode(b64.GetString());
                            if (decoded is not null) return decoded;
                        }
                    }
                }

                // Иногда картинка лежит в content как data URL.
                if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString() ?? "";
                    var idx = text.IndexOf("data:image/", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        var decoded = DecodeDataUrl(ExtractDataUrl(text, idx));
                        if (decoded is not null) return decoded;
                    }
                }
            }
        }
        catch { /* ignore parse */ }

        return null;
    }

    private static string BuildPrompt(ImageGenRequest r)
    {
        var style = string.IsNullOrWhiteSpace(r.StyleHint) ? "pixel art game sprite" : r.StyleHint;
        return $"""
            Create a single {style} asset for a 2D MonoGame game.
            Asset id: {r.AssetId}
            Kind: {r.Kind}
            Description: {r.Prompt}
            Target size about {r.Width}x{r.Height} pixels.
            Transparent background if possible. Centered subject. No text, no watermark, no UI chrome.
            """;
    }

    private static byte[]? DecodeDataUrl(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().Trim('"');
        const string marker = "base64,";
        var i = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i >= 0) return Decode(s[(i + marker.Length)..]);
        // raw base64?
        if (s.Length > 64 && !s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return Decode(s);
        return null;
    }

    private static string ExtractDataUrl(string text, int start)
    {
        int end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ')' and not '"' and not '\'')
            end++;
        return text[start..end];
    }

    private static byte[]? Decode(string? b64)
    {
        if (string.IsNullOrWhiteSpace(b64)) return null;
        try
        {
            var clean = b64.Trim().Replace("\n", "").Replace("\r", "");
            return Convert.FromBase64String(clean);
        }
        catch { return null; }
    }

    private static void ApplyAuth(HttpRequestMessage req, string key)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/ai-it-company");
        req.Headers.TryAddWithoutValidation("X-Title", "AI IT Company");
    }

    private static HttpClient Create(string baseUrl) => new()
    {
        BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"),
        Timeout = TimeSpan.FromMinutes(3)
    };

    private static string Normalize(string url)
        => string.IsNullOrWhiteSpace(url) ? "https://openrouter.ai/api/v1" : url.Trim().TrimEnd('/');

    private static string Trim(string s) => s.Length <= 400 ? s : s[..400] + "…";

    public void Dispose() => _http.Dispose();
}
