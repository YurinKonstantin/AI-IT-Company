using Ai;
using Core.Configuration;
using Core.Freelance;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ai.Freelance;

/// <summary>
/// Адаптер FL.ru: если задан feed URL (+ опциональный API key), читает JSON-ленту.
/// Иначе возвращает пустой список (без скрапинга).
/// Ожидаемый JSON: { "items": [ { "id","title","description","budgetMin","budgetMax","currency","tags","url" } ] }
/// </summary>
public sealed class FlRuMarketplaceAdapter : IFreelanceMarketplace, IDisposable
{
    public const string FeedUrlKey = "FreelanceFlRuFeedUrl";
    private readonly WindowsCredentialStore _creds;
    private readonly AppSettingsStore _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(40) };

    public string Name => "FlRu";

    public FlRuMarketplaceAdapter(WindowsCredentialStore creds, AppSettingsStore settings)
    {
        _creds = creds;
        _settings = settings;
    }

    public async Task<IReadOnlyList<FreelanceJobOffer>> SearchAsync(FreelanceSearchQuery query, CancellationToken ct = default)
    {
        var feed = _settings.Get(FeedUrlKey, "");
        if (string.IsNullOrWhiteSpace(feed))
            return Array.Empty<FreelanceJobOffer>();

        using var req = new HttpRequestMessage(HttpMethod.Get, feed);
        var key = _creds.GetSecret(WindowsCredentialStore.FlRuApiResource);
        if (!string.IsNullOrWhiteSpace(key))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return Array.Empty<FreelanceJobOffer>();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return ParseItems(doc.RootElement, query);
    }

    public Task<FreelanceJobOffer?> GetDetailsAsync(string externalId, CancellationToken ct = default)
        => Task.FromResult<FreelanceJobOffer?>(null);

    private static IReadOnlyList<FreelanceJobOffer> ParseItems(JsonElement root, FreelanceSearchQuery query)
    {
        if (!root.TryGetProperty("items", out var items)) return Array.Empty<FreelanceJobOffer>();
        var list = new List<FreelanceJobOffer>();
        foreach (var it in items.EnumerateArray())
        {
            var title = it.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(title)) continue;
            var offer = new FreelanceJobOffer
            {
                ExternalId = it.TryGetProperty("id", out var id) ? id.ToString() : Guid.NewGuid().ToString("N"),
                Source = "FlRu",
                Title = title,
                Description = it.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                BudgetMin = ReadDecimal(it, "budgetMin"),
                BudgetMax = ReadDecimal(it, "budgetMax"),
                Currency = it.TryGetProperty("currency", out var c) ? c.GetString() ?? "RUB" : "RUB",
                Url = it.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                Tags = ReadTags(it)
            };
            if (query.MinBudget is { } mb && (offer.BudgetMid ?? 0) < mb) continue;
            list.Add(offer);
            if (list.Count >= query.MaxResults) break;
        }
        return list;
    }

    private static decimal? ReadDecimal(JsonElement it, string name)
    {
        if (!it.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d)) return d;
        if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), out var s)) return s;
        return null;
    }

    private static List<string> ReadTags(JsonElement it)
    {
        var tags = new List<string> { "fl.ru" };
        if (!it.TryGetProperty("tags", out var arr) || arr.ValueKind != JsonValueKind.Array) return tags;
        foreach (var t in arr.EnumerateArray())
            if (t.GetString() is { } s) tags.Add(s);
        return tags;
    }

    public void Dispose() => _http.Dispose();
}
