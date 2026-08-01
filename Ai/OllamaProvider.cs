using Core.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ai;

public sealed class OllamaProvider : IAiProvider, IDisposable
{
    private HttpClient _http;
    private readonly object _httpLock = new();

    public string Name => "Ollama";
    public string BaseUrl { get; private set; }

    public OllamaProvider(string baseUrl = "http://localhost:11434")
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
            var r = await http.GetAsync("/api/tags", ct);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async IAsyncEnumerable<string> GenerateStreamAsync(
        AiRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var http = SnapshotHttp();
        var body = new
        {
            model = request.ModelName,
            prompt = request.UserPrompt,
            system = request.SystemPrompt,
            stream = true,
            options = new
            {
                temperature = request.Temperature,
                num_ctx = request.ContextWindow,
                num_predict = request.MaxTokens == 0 ? -1 : request.MaxTokens
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/generate")
        { Content = JsonContent.Create(body) };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("response", out var r))
                yield return r.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("done", out var d) && d.GetBoolean()) break;
        }
    }

    public void Dispose() => _http.Dispose();

    private HttpClient SnapshotHttp()
    {
        lock (_httpLock) return _http;
    }

    private static string Normalize(string baseUrl)
        => (baseUrl ?? "").Trim().TrimEnd('/');

    private static HttpClient Create(string url) => new()
    {
        BaseAddress = new Uri(url),
        Timeout = Timeout.InfiniteTimeSpan
    };
}
