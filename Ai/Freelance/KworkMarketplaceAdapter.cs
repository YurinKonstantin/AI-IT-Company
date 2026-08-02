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
/// Адаптер Kwork: JSON feed URL из настроек (+ API key в vault). Без скрапинга.
/// </summary>
public sealed class KworkMarketplaceAdapter : IFreelanceMarketplace, IDisposable
{
    public const string FeedUrlKey = "FreelanceKworkFeedUrl";
    private readonly WindowsCredentialStore _creds;
    private readonly AppSettingsStore _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(40) };

    public string Name => "Kwork";

    public KworkMarketplaceAdapter(WindowsCredentialStore creds, AppSettingsStore settings)
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
        var key = _creds.GetSecret(WindowsCredentialStore.KworkApiResource);
        if (!string.IsNullOrWhiteSpace(key))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return Array.Empty<FreelanceJobOffer>();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("items", out var items))
            return Array.Empty<FreelanceJobOffer>();

        var list = new List<FreelanceJobOffer>();
        foreach (var it in items.EnumerateArray())
        {
            var title = it.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(title)) continue;
            list.Add(new FreelanceJobOffer
            {
                ExternalId = it.TryGetProperty("id", out var id) ? id.ToString() : Guid.NewGuid().ToString("N"),
                Source = Name,
                Title = title,
                Description = it.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                BudgetMin = ReadDecimal(it, "budgetMin"),
                BudgetMax = ReadDecimal(it, "budgetMax"),
                Currency = it.TryGetProperty("currency", out var c) ? c.GetString() ?? "RUB" : "RUB",
                Url = it.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                Tags = new List<string> { "kwork" }
            });
            if (list.Count >= query.MaxResults) break;
        }
        return list;
    }

    public Task<FreelanceJobOffer?> GetDetailsAsync(string externalId, CancellationToken ct = default)
        => Task.FromResult<FreelanceJobOffer?>(null);

    private static decimal? ReadDecimal(JsonElement it, string name)
    {
        if (!it.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d)) return d;
        if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), out var s)) return s;
        return null;
    }

    public void Dispose() => _http.Dispose();
}
