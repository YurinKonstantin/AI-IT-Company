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

public sealed class OllamaProvider : IAiProvider
{
    private readonly HttpClient _http;
    public string Name => "Ollama";

    public OllamaProvider(string baseUrl = "http://localhost:11434")
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try { var r = await _http.GetAsync("/api/tags", ct); return r.IsSuccessStatusCode; }
        catch { return false; }
    }

    public async IAsyncEnumerable<string> GenerateStreamAsync(
        AiRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
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
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
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
}