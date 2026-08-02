using Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Freelance;

public sealed class FreelanceJobStore
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public FreelanceJobStore(IDbContextFactory<AppDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS FreelanceJobs (
                Id TEXT NOT NULL PRIMARY KEY,
                ExternalId TEXT NOT NULL,
                Source TEXT NOT NULL,
                Title TEXT NOT NULL,
                Description TEXT NOT NULL,
                Url TEXT NOT NULL,
                Currency TEXT NOT NULL,
                BudgetMin TEXT NULL,
                BudgetMax TEXT NULL,
                Tags TEXT NOT NULL,
                Status TEXT NOT NULL,
                FeasibilityScore INTEGER NOT NULL,
                FinalScore INTEGER NOT NULL,
                ExpectedEffortHours REAL NOT NULL,
                ExpectedRevenue TEXT NOT NULL,
                ExpectedProfit TEXT NOT NULL,
                RiskFlags TEXT NOT NULL,
                Explain TEXT NOT NULL,
                Recommendation TEXT NOT NULL,
                PipelineProjectId TEXT NULL,
                OutputRoot TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """, ct);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS FreelanceOutcomes (
                Id TEXT NOT NULL PRIMARY KEY,
                JobId TEXT NOT NULL,
                Success INTEGER NOT NULL,
                Rating INTEGER NOT NULL,
                ActualHours REAL NULL,
                ActualRevenue TEXT NULL,
                Notes TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            """, ct);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS FreelanceSkillStats (
                Tag TEXT NOT NULL PRIMARY KEY,
                Attempts INTEGER NOT NULL,
                Successes INTEGER NOT NULL,
                AvgRating REAL NOT NULL,
                AvgProfit TEXT NOT NULL,
                FeasibilityBias REAL NOT NULL DEFAULT 0
            );
            """, ct);
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
        catch { /* already exists */ }
    }

    public async Task UpsertOfferAsync(FreelanceJobOffer offer, FreelanceScoreResult score, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var id = $"{offer.Source}:{offer.ExternalId}";
        var row = await db.FreelanceJobs.FindAsync(new object[] { id }, ct);
        if (row is null)
        {
            row = new FreelanceJobRecord { Id = id, CreatedAt = DateTime.UtcNow };
            db.FreelanceJobs.Add(row);
        }

        row.ExternalId = offer.ExternalId;
        row.Source = offer.Source;
        row.Title = offer.Title;
        row.Description = offer.Description;
        row.Url = offer.Url;
        row.Currency = offer.Currency;
        row.BudgetMin = offer.BudgetMin?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        row.BudgetMax = offer.BudgetMax?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        row.Tags = string.Join(',', offer.Tags);
        if (row.Status is null or "" or nameof(FreelanceJobStatus.New))
            row.Status = nameof(FreelanceJobStatus.New);
        row.FeasibilityScore = score.FeasibilityScore;
        row.FinalScore = score.FinalScore;
        row.ExpectedEffortHours = score.ExpectedEffortHours;
        row.ExpectedRevenue = score.ExpectedRevenue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        row.ExpectedProfit = score.ExpectedProfit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        row.RiskFlags = string.Join('|', score.RiskFlags);
        row.Explain = score.Explain;
        row.Recommendation = score.Recommendation.ToString();
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<FreelanceJobRecord>> ListAsync(
        string? statusFilter = null, int take = 80, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = db.FreelanceJobs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(statusFilter))
            q = q.Where(j => j.Status == statusFilter);
        return await q.OrderByDescending(j => j.UpdatedAt).Take(take).ToListAsync(ct);
    }

    public async Task<FreelanceJobRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.FreelanceJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
    }

    public async Task SetStatusAsync(
        string id,
        FreelanceJobStatus status,
        string? pipelineProjectId = null,
        string? outputRoot = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.FreelanceJobs.FindAsync(new object[] { id }, ct);
        if (row is null) return;
        row.Status = status.ToString();
        if (pipelineProjectId is not null) row.PipelineProjectId = pipelineProjectId;
        if (outputRoot is not null) row.OutputRoot = outputRoot;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveOutcomeAsync(
        string jobId, bool success, int rating, double? actualHours, decimal? actualRevenue, string notes,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.FreelanceOutcomes.Add(new FreelanceOutcomeRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            JobId = jobId,
            Success = success,
            Rating = Math.Clamp(rating, 1, 5),
            ActualHours = actualHours,
            ActualRevenue = actualRevenue?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Notes = notes ?? "",
            CreatedAt = DateTime.UtcNow
        });

        var job = await db.FreelanceJobs.FindAsync(new object[] { jobId }, ct);
        if (job is not null)
        {
            job.Status = success ? nameof(FreelanceJobStatus.Succeeded) : nameof(FreelanceJobStatus.Failed);
            job.UpdatedAt = DateTime.UtcNow;

            foreach (var tag in job.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var key = tag.ToLowerInvariant();
                var stat = await db.FreelanceSkillStats.FindAsync(new object[] { key }, ct);
                if (stat is null)
                {
                    stat = new FreelanceSkillStatRecord { Tag = key };
                    db.FreelanceSkillStats.Add(stat);
                }
                stat.Attempts++;
                if (success) stat.Successes++;
                stat.AvgRating = ((stat.AvgRating * (stat.Attempts - 1)) + rating) / stat.Attempts;
                if (actualRevenue is { } ar)
                {
                    decimal.TryParse(stat.AvgProfit, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var prev);
                    var avg = ((prev * (stat.Attempts - 1)) + ar) / stat.Attempts;
                    stat.AvgProfit = avg.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<Dictionary<string, double>> GetTagSuccessRatesAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.FreelanceSkillStats.AsNoTracking().ToListAsync(ct);
        return rows.ToDictionary(
            r => r.Tag,
            r => r.Attempts == 0 ? 0.5 : (double)r.Successes / r.Attempts,
            StringComparer.OrdinalIgnoreCase);
    }
}
