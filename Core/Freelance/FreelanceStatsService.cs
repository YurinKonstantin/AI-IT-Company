using Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Freelance;

public sealed record FreelanceDashboardStats(
    int Outcomes,
    int Successes,
    double WinRate,
    double AvgRating,
    decimal ExpectedProfitSum,
    decimal ActualRevenueSum,
    decimal ProfitDelta,
    IReadOnlyList<FreelanceTagStatVm> TopTags,
    IReadOnlyList<string> RecentAudit);

public sealed record FreelanceTagStatVm(
    string Tag,
    int Attempts,
    int Successes,
    double WinRate,
    double AvgRating,
    decimal AvgProfit,
    double FeasibilityBias);

/// <summary>Агрегаты биржи для UI и калибровки.</summary>
public sealed class FreelanceStatsService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public FreelanceStatsService(IDbContextFactory<AppDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS FreelanceAudits (
                Id TEXT NOT NULL PRIMARY KEY,
                Action TEXT NOT NULL,
                JobId TEXT NOT NULL,
                Details TEXT NOT NULL,
                At TEXT NOT NULL
            );
            """, ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE FreelanceSkillStats ADD COLUMN FeasibilityBias REAL NOT NULL DEFAULT 0;", ct);
        }
        catch
        {
            /* column may already exist */
        }
    }

    public async Task<FreelanceDashboardStats> GetDashboardAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var outcomes = await db.FreelanceOutcomes.AsNoTracking().ToListAsync(ct);
        var jobs = await db.FreelanceJobs.AsNoTracking().ToListAsync(ct);
        var tags = await db.FreelanceSkillStats.AsNoTracking().ToListAsync(ct);
        var audits = await db.FreelanceAudits.AsNoTracking()
            .OrderByDescending(a => a.At).Take(12).ToListAsync(ct);

        int successes = outcomes.Count(o => o.Success);
        double win = outcomes.Count == 0 ? 0 : (double)successes / outcomes.Count;
        double avgRating = outcomes.Count == 0 ? 0 : outcomes.Average(o => o.Rating);

        decimal expected = 0;
        foreach (var j in jobs.Where(j =>
                     j.Status is nameof(FreelanceJobStatus.Succeeded)
                         or nameof(FreelanceJobStatus.Failed)
                         or nameof(FreelanceJobStatus.AwaitingUserReview)
                         or nameof(FreelanceJobStatus.Running)))
        {
            if (decimal.TryParse(j.ExpectedProfit, NumberStyles.Any, CultureInfo.InvariantCulture, out var ep))
                expected += ep;
        }

        decimal actual = 0;
        foreach (var o in outcomes)
        {
            if (decimal.TryParse(o.ActualRevenue, NumberStyles.Any, CultureInfo.InvariantCulture, out var ar))
                actual += ar;
        }

        var top = tags
            .OrderByDescending(t => t.Attempts)
            .ThenByDescending(t => t.Successes)
            .Take(8)
            .Select(t => new FreelanceTagStatVm(
                t.Tag,
                t.Attempts,
                t.Successes,
                t.Attempts == 0 ? 0 : (double)t.Successes / t.Attempts,
                t.AvgRating,
                decimal.TryParse(t.AvgProfit, NumberStyles.Any, CultureInfo.InvariantCulture, out var ap) ? ap : 0,
                t.FeasibilityBias))
            .ToList();

        return new FreelanceDashboardStats(
            outcomes.Count,
            successes,
            win,
            avgRating,
            Math.Round(expected, 2),
            Math.Round(actual, 2),
            Math.Round(actual - expected, 2),
            top,
            audits.Select(a => $"{a.At.ToLocalTime():g} {a.Action} {a.JobId} — {a.Details}").ToList());
    }

    public async Task WriteAuditAsync(string action, string jobId, string details, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.FreelanceAudits.Add(new FreelanceAuditRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Action = action,
            JobId = jobId ?? "",
            Details = Truncate(details ?? "", 500),
            At = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// EMA-калибровка: успех с высоким rating поднимает bias, провал опускает.
    /// </summary>
    public async Task ApplyOutcomeCalibrationAsync(
        string jobId, bool success, int rating, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var job = await db.FreelanceJobs.FindAsync(new object[] { jobId }, ct);
        if (job is null) return;

        // signal: success★5 → +1, success★1 → +0.2, fail★1 → -1, fail★5 → -0.4
        double signal = success
            ? 0.2 + 0.8 * ((Math.Clamp(rating, 1, 5) - 1) / 4.0)
            : - (0.4 + 0.6 * ((5 - Math.Clamp(rating, 1, 5)) / 4.0));

        const double alpha = 0.25; // EMA
        foreach (var tag in job.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var key = tag.ToLowerInvariant();
            var stat = await db.FreelanceSkillStats.FindAsync(new object[] { key }, ct);
            if (stat is null)
            {
                stat = new FreelanceSkillStatRecord { Tag = key };
                db.FreelanceSkillStats.Add(stat);
            }

            var target = signal * 12; // scale to roughly ±12 points
            stat.FeasibilityBias = Math.Clamp(
                (1 - alpha) * stat.FeasibilityBias + alpha * target,
                -20, 20);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<Dictionary<string, double>> GetTagBiasesAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.FreelanceSkillStats.AsNoTracking().ToListAsync(ct);
        return rows.ToDictionary(r => r.Tag, r => r.FeasibilityBias, StringComparer.OrdinalIgnoreCase);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
