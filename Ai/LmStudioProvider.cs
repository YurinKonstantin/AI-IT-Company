using Core.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ai;

/// <summary>
/// OpenAI-compatible клиент для LM Studio (локальный сервер, обычно http://localhost:1234/v1).
/// API key не обязателен; при необходимости отправляется Bearer lm-studio.
/// </summary>
public sealed class LmStudioProvider : IAiProvider, IDisposable
{
    private HttpClient _http;
    private readonly object _httpLock = new();

    public string Name => "LM Studio";
    public string BaseUrl { get; private set; }

    public LmStudioProvider(string baseUrl = "http://localhost:1234/v1")
    {
        BaseUrl = Normalize(baseUrl);
        _http = Create(BaseUrl);
    }

    public void SetBaseUrl(string baseUrl)
    {
        var url = Normalize(baseUrl);
        lock (_httpLock)
        {
            if (string.Equals(BaseUrl, url, StringComparison.OrdinalIgnoreCase))
                return;
            BaseUrl = url;
            _http.Dispose();
            _http = Create(url);
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var http = SnapshotHttp();
            using var req = new HttpRequestMessage(HttpMethod.Get, "models");
            ApplyAuth(req);
            using var resp = await http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async IAsyncEnumerable<string> GenerateStreamAsync(
        AiRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var http = SnapshotHttp();
        var maxTokens = request.MaxTokens <= 0 ? (int?)null : request.MaxTokens;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.ModelName,
            ["stream"] = true,
            ["temperature"] = request.Temperature,
            ["messages"] = new object[]
            {
                new { role = "system", content = request.SystemPrompt ?? "" },
                new { role = "user", content = request.UserPrompt ?? "" }
            }
        };
        if (maxTokens is not null)
            payload["max_tokens"] = maxTokens.Value;

        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };
        ApplyAuth(req);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"LM Studio HTTP {(int)resp.StatusCode}: {Truncate(err, 500)}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            var data = line["data:".Length..].Trim();
            if (data is "[DONE]") break;

            string? piece = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("choices", out var choices)
                    && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("content", out var content))
                        piece = content.GetString();
                }
            }
            catch
            {
                /* skip malformed SSE chunk */
            }

            if (!string.IsNullOrEmpty(piece))
                yield return piece;
        }
    }

    public void Dispose() => _http.Dispose();

    private HttpClient SnapshotHttp()
    {
        lock (_httpLock) return _http;
    }

    private static void ApplyAuth(HttpRequestMessage req)
    {
        // LM Studio принимает любой/пустой ключ; фиксируем совместимый Bearer.
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "lm-studio");
    }

    private static string Normalize(string baseUrl)
        => (baseUrl ?? "").Trim().TrimEnd('/');

    private static HttpClient Create(string url) => new()
    {
        BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/"),
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}
