using Ai;
using Core.Configuration;
using Core.Freelance;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ai.Freelance;

/// <summary>
/// Платные задачи с GitHub: Issues с bounty/funded labels через Search API.
/// </summary>
public sealed class GitHubBountyMarketplaceAdapter : IFreelanceMarketplace, IDisposable
{
    private readonly WindowsCredentialStore _creds;
    private readonly AppSettingsStore _settings;
    private readonly HttpClient _http;

    public string Name => "GitHub";

    public GitHubBountyMarketplaceAdapter(WindowsCredentialStore creds, AppSettingsStore settings)
    {
        _creds = creds;
        _settings = settings;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(45)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AI-IT-Company-Freelance");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<IReadOnlyList<FreelanceJobOffer>> SearchAsync(FreelanceSearchQuery query, CancellationToken ct = default)
    {
        var q = _settings.Get(AppSettingsStore.KeyFreelanceGitHubQuery, AppSettingsStore.KeyFreelanceGitHubQueryDefault);
        if (!string.IsNullOrWhiteSpace(query.Keywords))
            q = $"{q} {query.Keywords}";

        ApplyAuth();
        var url = $"search/issues?q={Uri.EscapeDataString(q)}&per_page={Math.Clamp(query.MaxResults, 5, 50)}&sort=updated";
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"GitHub search HTTP {(int)resp.StatusCode}: {Truncate(err, 300)}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("items", out var items))
            return Array.Empty<FreelanceJobOffer>();

        var list = new List<FreelanceJobOffer>();
        foreach (var item in items.EnumerateArray())
        {
            var title = item.GetProperty("title").GetString() ?? "";
            var body = item.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var htmlUrl = item.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : Guid.NewGuid().ToString("N");
            var labels = new List<string> { "github", "bounty" };
            if (item.TryGetProperty("labels", out var labs))
            {
                foreach (var lab in labs.EnumerateArray())
                {
                    if (lab.TryGetProperty("name", out var n) && n.GetString() is { } name)
                        labels.Add(name);
                }
            }

            var (min, max, currency) = ParseBudget(title + "\n" + body);
            if (query.MinBudget is { } mb && (max ?? min ?? 0) < mb) continue;

            string? client = null;
            if (item.TryGetProperty("repository_url", out var repoUrl)
                && repoUrl.GetString() is { } ru)
            {
                client = ru.TrimEnd('/').Split('/').LastOrDefault();
            }

            list.Add(new FreelanceJobOffer
            {
                ExternalId = id,
                Source = Name,
                Title = title,
                Description = Truncate(body, 4000),
                BudgetMin = min,
                BudgetMax = max,
                Currency = currency,
                Tags = labels.Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList(),
                Url = htmlUrl,
                ClientName = client,
                RawJson = item.GetRawText()
            });
        }

        return list;
    }

    public async Task<FreelanceJobOffer?> GetDetailsAsync(string externalId, CancellationToken ct = default)
    {
        var found = await SearchAsync(new FreelanceSearchQuery { MaxResults = 50 }, ct);
        return found.FirstOrDefault(o => o.ExternalId == externalId);
    }

    private void ApplyAuth()
    {
        _http.DefaultRequestHeaders.Authorization = null;
        var pat = _creds.GetSecret(WindowsCredentialStore.GitHubPatResource);
        if (!string.IsNullOrWhiteSpace(pat))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
    }

    private static (decimal? Min, decimal? Max, string Currency) ParseBudget(string text)
    {
        var m = Regex.Match(text,
            @"\$\s*(\d{2,6}(?:\.\d{1,2})?)(?:\s*[-–]\s*\$?\s*(\d{2,6}(?:\.\d{1,2})?))?|bounty\s*[:=]?\s*\$?\s*(\d{2,6})",
            RegexOptions.IgnoreCase);
        if (!m.Success) return (null, null, "USD");

        decimal? a = null, b = null;
        if (m.Groups[1].Success && decimal.TryParse(m.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v1))
            a = v1;
        if (m.Groups[2].Success && decimal.TryParse(m.Groups[2].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v2))
            b = v2;
        if (m.Groups[3].Success && decimal.TryParse(m.Groups[3].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v3))
            a ??= v3;

        return (a, b ?? a, "USD");
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    public void Dispose() => _http.Dispose();
}
