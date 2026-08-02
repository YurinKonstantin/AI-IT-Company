using Core.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Freelance;

public sealed class FreelanceHuntService
{
    private readonly IEnumerable<IFreelanceMarketplace> _markets;
    private readonly FreelanceOpportunityScorer _scorer;
    private readonly FreelanceJobStore _store;
    private readonly FreelanceStatsService _stats;
    private readonly AppSettingsStore _settings;

    public FreelanceHuntService(
        IEnumerable<IFreelanceMarketplace> markets,
        FreelanceOpportunityScorer scorer,
        FreelanceJobStore store,
        FreelanceStatsService stats,
        AppSettingsStore settings)
    {
        _markets = markets;
        _scorer = scorer;
        _store = store;
        _stats = stats;
        _settings = settings;
    }

    public bool IsSimulationOnly =>
        bool.TryParse(_settings.Get(AppSettingsStore.KeyFreelanceSimulationOnly, "false"), out var s) && s;

    public async Task<(int Offers, int Saved, int Deduped, string Log, IReadOnlyList<string> AutoCandidateIds)> HuntAsync(
        CancellationToken ct = default)
    {
        if (!TryAcquireHuntSlot(out var waitSec))
        {
            var msg = $"Rate limit: подождите ещё {waitSec}с перед следующей охотой.";
            await _stats.WriteAuditAsync("HuntRateLimited", "", msg, ct);
            return (0, 0, 0, msg, Array.Empty<string>());
        }

        var enabled = GetEnabledSources();
        var keywords = _settings.Get(AppSettingsStore.KeyFreelanceKeywords, AppSettingsStore.KeyFreelanceKeywordsDefault);
        var query = new FreelanceSearchQuery
        {
            Keywords = keywords,
            MaxResults = 30,
            Sources = enabled
        };

        var rates = await _store.GetTagSuccessRatesAsync(ct);
        var biases = await _stats.GetTagBiasesAsync(ct);
        var log = new System.Text.StringBuilder();
        if (IsSimulationOnly)
            log.AppendLine("SIMULATION: hunt+score only, auto-accept отключён.");

        int offers = 0, saved = 0, deduped = 0;
        var autoCandidates = new List<string>();

        foreach (var market in _markets.Where(m => enabled.Contains(m.Name, StringComparer.OrdinalIgnoreCase)))
        {
            try
            {
                var list = await market.SearchAsync(query, ct);
                offers += list.Count;
                log.AppendLine($"{market.Name}: {list.Count} офферов");
                foreach (var offer in list)
                {
                    var id = $"{offer.Source}:{offer.ExternalId}";
                    var existing = await _store.GetAsync(id, ct);
                    if (existing is not null
                        && existing.Status is not nameof(FreelanceJobStatus.New)
                            and not nameof(FreelanceJobStatus.Skipped))
                    {
                        deduped++;
                        continue;
                    }

                    var score = _scorer.Score(offer, rates, biases);
                    await _store.UpsertOfferAsync(offer, score, ct);
                    saved++;

                    if (!IsSimulationOnly && ShouldAutoAccept(score))
                        autoCandidates.Add(id);
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"{market.Name}: ошибка — {ex.Message}");
            }
        }

        await _settings.SetAsync(AppSettingsStore.KeyFreelanceLastHuntUtc, DateTime.UtcNow.ToString("O"));
        await _stats.WriteAuditAsync("Hunt", "",
            $"offers={offers} saved={saved} dedup={deduped} sources={string.Join(',', enabled)}", ct);

        return (offers, saved, deduped, log.ToString(), autoCandidates);
    }

    public IReadOnlyList<string> GetEnabledSources()
    {
        var list = new List<string>();
        if (IsOn(AppSettingsStore.KeyFreelanceEnableDemo, defaultOn: true)) list.Add("Demo");
        if (IsOn(AppSettingsStore.KeyFreelanceEnableGitHub, defaultOn: true)) list.Add("GitHub");
        if (IsOn(AppSettingsStore.KeyFreelanceEnableFlRu, defaultOn: false)) list.Add("FlRu");
        if (IsOn(AppSettingsStore.KeyFreelanceEnableKwork, defaultOn: false)) list.Add("Kwork");
        return list;
    }

    public bool ShouldAutoAccept(FreelanceScoreResult score)
    {
        if (IsSimulationOnly) return false;
        if (!bool.TryParse(_settings.Get(AppSettingsStore.KeyFreelanceAutoAccept, "false"), out var on) || !on)
            return false;
        if (!int.TryParse(_settings.Get(AppSettingsStore.KeyFreelanceAutoAcceptThreshold, AppSettingsStore.KeyFreelanceAutoAcceptThresholdDefault), out var thr))
            thr = 75;
        if (!decimal.TryParse(_settings.Get(AppSettingsStore.KeyFreelanceMinProfit, AppSettingsStore.KeyFreelanceMinProfitDefault),
                NumberStyles.Number, CultureInfo.InvariantCulture, out var minProfit))
            minProfit = 50;

        return score.FinalScore >= thr
               && score.ExpectedProfit >= minProfit
               && score.Recommendation == FreelanceRecommendation.Accept
               && !score.RiskFlags.Any(r => r.Contains("жёсткий", StringComparison.OrdinalIgnoreCase));
    }

    private bool TryAcquireHuntSlot(out int waitSeconds)
    {
        waitSeconds = 0;
        if (!int.TryParse(_settings.Get(AppSettingsStore.KeyFreelanceHuntMinIntervalSec,
                AppSettingsStore.KeyFreelanceHuntMinIntervalSecDefault), out var minSec))
            minSec = 30;

        var raw = _settings.Get(AppSettingsStore.KeyFreelanceLastHuntUtc, "");
        if (DateTime.TryParse(raw, null, DateTimeStyles.RoundtripKind, out var last))
        {
            var elapsed = (DateTime.UtcNow - last.ToUniversalTime()).TotalSeconds;
            if (elapsed < minSec)
            {
                waitSeconds = (int)Math.Ceiling(minSec - elapsed);
                return false;
            }
        }
        return true;
    }

    private bool IsOn(string key, bool defaultOn)
    {
        var raw = _settings.Get(key, defaultOn ? "true" : "false");
        return bool.TryParse(raw, out var b) ? b : defaultOn;
    }
}
