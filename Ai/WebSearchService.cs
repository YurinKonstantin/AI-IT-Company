using Ai;
using Core.Configuration;
using Core.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ai;

/// <summary>
/// Tavily or Brave web search with hard budget (1–3 snippets). Failures are soft (empty block).
/// </summary>
public sealed class WebSearchService : IWebSearchService, IDisposable
{
    private readonly AppSettingsStore _settings;
    private readonly WindowsCredentialStore _creds;
    private readonly ILogger<WebSearchService> _logger;
    private readonly HttpClient _http;

    public WebSearchService(
        AppSettingsStore settings,
        WindowsCredentialStore creds,
        ILogger<WebSearchService> logger)
    {
        _settings = settings;
        _creds = creds;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<string> BuildPromptBlockAsync(string query, CancellationToken ct = default)
    {
        if (!_settings.GetWebSearchEnabled()) return "";
        if (string.IsNullOrWhiteSpace(query)) return "";

        var max = _settings.GetWebSearchMaxSnippets();
        var budget = _settings.GetWebSearchBudgetChars();
        var provider = _settings.GetWebSearchProvider();

        try
        {
            IReadOnlyList<(string Title, string Url, string Snippet)> hits =
                string.Equals(provider, "Brave", StringComparison.OrdinalIgnoreCase)
                    ? await SearchBraveAsync(query, max, ct)
                    : await SearchTavilyAsync(query, max, ct);

            if (hits.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("=== WEB SEARCH (opt-in, budgeted — treat as hints, verify) ===");
            int used = sb.Length;
            foreach (var (title, url, snippet) in hits)
            {
                var line = $"- {title}\n  {url}\n  {Truncate(snippet, 400)}\n";
                if (used + line.Length > budget) break;
                sb.Append(line);
                used += line.Length;
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Web search failed ({Provider})", provider);
            return "";
        }
    }

    private async Task<IReadOnlyList<(string, string, string)>> SearchTavilyAsync(
        string query, int max, CancellationToken ct)
    {
        var key = _creds.GetSecret(WindowsCredentialStore.TavilyResource);
        if (string.IsNullOrWhiteSpace(key)) return Array.Empty<(string, string, string)>();

        var body = new
        {
            api_key = key,
            query,
            max_results = max,
            search_depth = "basic",
            include_answer = false
        };

        using var resp = await _http.PostAsJsonAsync("https://api.tavily.com/search", body, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Tavily HTTP {Code}: {Err}", (int)resp.StatusCode, Truncate(err, 200));
            return Array.Empty<(string, string, string)>();
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var list = new List<(string, string, string)>();
        if (!doc.RootElement.TryGetProperty("results", out var results)) return list;

        foreach (var r in results.EnumerateArray())
        {
            var title = r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var url = r.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var content = r.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(content)) continue;
            list.Add((title, url, content));
            if (list.Count >= max) break;
        }

        return list;
    }

    private async Task<IReadOnlyList<(string, string, string)>> SearchBraveAsync(
        string query, int max, CancellationToken ct)
    {
        var key = _creds.GetSecret(WindowsCredentialStore.BraveSearchResource);
        if (string.IsNullOrWhiteSpace(key)) return Array.Empty<(string, string, string)>();

        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.search.brave.com/res/v1/web/search?q=" + Uri.EscapeDataString(query)
            + "&count=" + max);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("X-Subscription-Token", key);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Brave HTTP {Code}: {Err}", (int)resp.StatusCode, Truncate(err, 200));
            return Array.Empty<(string, string, string)>();
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var list = new List<(string, string, string)>();
        if (!doc.RootElement.TryGetProperty("web", out var web)
            || !web.TryGetProperty("results", out var results))
            return list;

        foreach (var r in results.EnumerateArray())
        {
            var title = r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var url = r.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var desc = r.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            list.Add((title, url, desc));
            if (list.Count >= max) break;
        }

        return list;
    }

    public void Dispose() => _http.Dispose();

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}
