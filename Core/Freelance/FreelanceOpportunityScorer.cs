using Core.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Core.Freelance;

public sealed class FreelanceOpportunityScorer
{
    private readonly AppSettingsStore _settings;

    public FreelanceOpportunityScorer(AppSettingsStore settings) => _settings = settings;

    public FreelanceScoreResult Score(
        FreelanceJobOffer offer,
        IReadOnlyDictionary<string, double>? tagSuccessRates = null,
        IReadOnlyDictionary<string, double>? tagBiases = null)
    {
        var skills = ParseSkills(_settings.Get(AppSettingsStore.KeyFreelanceSkills, AppSettingsStore.KeyFreelanceSkillsDefault));
        var hourly = ParseDecimal(_settings.Get(AppSettingsStore.KeyFreelanceHourlyRate, AppSettingsStore.KeyFreelanceHourlyRateDefault), 25m);

        var text = (offer.Title + " " + offer.Description + " " + string.Join(' ', offer.Tags)).ToLowerInvariant();
        int skillHits = skills.Count(s => text.Contains(s, StringComparison.OrdinalIgnoreCase));
        double skillRatio = skills.Count == 0 ? 0.4 : (double)skillHits / skills.Count;

        bool clarity = offer.Description.Length >= 120
            || offer.Tags.Count >= 2
            || Regex.IsMatch(offer.Description, @"\b(api|winui|monogame|service|entity|\.net)\b", RegexOptions.IgnoreCase);

        double deadlineSlack = 0.6;
        if (offer.DeadlineUtc is { } dl)
        {
            var hours = (dl - DateTime.UtcNow).TotalHours;
            deadlineSlack = hours < 24 ? 0.15 : hours < 72 ? 0.4 : hours < 168 ? 0.7 : 0.9;
        }

        double hist = 0.5;
        if (tagSuccessRates is not null && offer.Tags.Count > 0)
        {
            var rates = offer.Tags
                .Select(t => tagSuccessRates.TryGetValue(t.ToLowerInvariant(), out var r) ? r : (double?)null)
                .Where(r => r is not null)
                .Select(r => r!.Value)
                .ToList();
            if (rates.Count > 0) hist = rates.Average();
        }

        int feasibilityRaw = Clamp((int)Math.Round(
            100 * (0.45 * skillRatio + 0.25 * (clarity ? 1 : 0.35) + 0.15 * deadlineSlack + 0.15 * hist)));

        double bias = 0;
        if (tagBiases is not null && offer.Tags.Count > 0)
        {
            var biases = offer.Tags
                .Select(t => tagBiases.TryGetValue(t.ToLowerInvariant(), out var b) ? b : (double?)null)
                .Where(b => b is not null)
                .Select(b => b!.Value)
                .ToList();
            if (biases.Count > 0) bias = biases.Average();
        }

        int feasibility = Clamp((int)Math.Round(feasibilityRaw + bias));

        double effort = EstimateEffortHours(offer, skillRatio, clarity);
        decimal revenue = offer.BudgetMid ?? EstimateBudgetFromText(offer) ?? hourly * (decimal)(effort * 0.8);
        decimal profit = revenue - hourly * (decimal)effort;

        var risks = new List<string>();
        if (skillRatio < 0.25) risks.Add("слабое совпадение стека");
        if (!clarity) risks.Add("неясное ТЗ");
        if (offer.BudgetMid is { } b && b < hourly * 2) risks.Add("низкий бюджет");
        if (offer.DeadlineUtc is { } d && (d - DateTime.UtcNow).TotalHours < 24) risks.Add("жёсткий дедлайн");
        if (string.Equals(offer.Source, "GitHub", StringComparison.OrdinalIgnoreCase)
            && offer.BudgetMid is null)
            risks.Add("бюджет GitHub не распознан");

        int profitNorm = Clamp((int)Math.Round((double)(profit / Math.Max(1m, hourly * 8)) * 50 + 50));
        int riskPenalty = Math.Min(40, risks.Count * 12);
        int finalScore = Clamp((int)Math.Round(0.55 * feasibility + 0.45 * profitNorm) - riskPenalty);

        var rec = finalScore >= 70 && profit >= 0 && !risks.Any(r => r.Contains("жёсткий"))
            ? FreelanceRecommendation.Accept
            : finalScore >= 45 ? FreelanceRecommendation.Review : FreelanceRecommendation.Skip;

        var explain = new StringBuilder();
        explain.AppendLine("### Почему такой score");
        explain.AppendLine($"- skills: {skillHits}/{skills.Count} (ratio {skillRatio:0.00})");
        explain.AppendLine($"- clarity: {clarity}, deadlineSlack: {deadlineSlack:0.00}, histSuccess: {hist:0.00}");
        explain.AppendLine($"- feasibility raw: {feasibilityRaw}, calibration bias: {bias:+0.0;-0.0;0} → {feasibility}");
        explain.AppendLine($"- effort≈{effort:0.0}h, revenue≈{revenue:0}, profit≈{profit:0}, profitNorm={profitNorm}");
        explain.AppendLine($"- riskPenalty={riskPenalty}, final={finalScore}, recommendation={rec}");
        if (risks.Count > 0)
            explain.AppendLine($"- risks: {string.Join("; ", risks)}");

        return new FreelanceScoreResult
        {
            FeasibilityScore = feasibility,
            ExpectedEffortHours = effort,
            ExpectedRevenue = Math.Round(revenue, 2),
            ExpectedProfit = Math.Round(profit, 2),
            RiskFlags = risks,
            Recommendation = rec,
            FinalScore = finalScore,
            Explain = explain.ToString().TrimEnd()
        };
    }

    private static double EstimateEffortHours(FreelanceJobOffer offer, double skillRatio, bool clarity)
    {
        double baseH = 6;
        if (offer.Description.Length > 800) baseH += 4;
        if (offer.Description.Length > 2000) baseH += 6;
        if (offer.Tags.Any(t => t.Contains("game", StringComparison.OrdinalIgnoreCase)
                             || t.Contains("monogame", StringComparison.OrdinalIgnoreCase)))
            baseH += 8;
        if (!clarity) baseH += 3;
        baseH *= 1.3 - 0.4 * skillRatio;
        return Math.Clamp(baseH, 2, 80);
    }

    private static decimal? EstimateBudgetFromText(FreelanceJobOffer offer)
    {
        var m = Regex.Match(offer.Title + "\n" + offer.Description,
            @"\$\s*(\d{2,6})|bounty\s*[:=]?\s*\$?\s*(\d{2,6})|(\d{3,6})\s*(usd|rub|₽)",
            RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        for (int i = 1; i <= 3; i++)
            if (m.Groups[i].Success && decimal.TryParse(m.Groups[i].Value, out var v))
                return v;
        return null;
    }

    private static List<string> ParseSkills(string raw)
        => raw.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .Where(s => s.Length >= 2)
            .Distinct()
            .ToList();

    private static decimal ParseDecimal(string raw, decimal fallback)
        => decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static int Clamp(int v) => Math.Max(0, Math.Min(100, v));
}
