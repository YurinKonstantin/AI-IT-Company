using Core.Configuration;
using Core.Contracts;
using Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Services;

public sealed class CorrectionLessonStore
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _schemaReady;

    public CorrectionLessonStore(IDbContextFactory<AppDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_schemaReady) return;
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS CorrectionLessons (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Role TEXT NOT NULL,
                    Kind TEXT NOT NULL,
                    WrongSummary TEXT NOT NULL,
                    CorrectGuidance TEXT NOT NULL,
                    ContextSnippet TEXT NOT NULL,
                    Tags TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UseCount INTEGER NOT NULL,
                    Enabled INTEGER NOT NULL
                );
                """, ct);
            _schemaReady = true;
        }
        finally { _gate.Release(); }
    }

    public async Task<CorrectionLessonRecord> AddAsync(
        string role,
        string kind,
        string wrongSummary,
        string correctGuidance,
        string? contextSnippet = null,
        string? tags = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(correctGuidance))
            throw new ArgumentException("CorrectGuidance is required.", nameof(correctGuidance));

        await InitializeAsync(ct);
        var row = new CorrectionLessonRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Role = string.IsNullOrWhiteSpace(role) ? "Any" : role.Trim(),
            Kind = string.IsNullOrWhiteSpace(kind) ? "ManualTeach" : kind.Trim(),
            WrongSummary = (wrongSummary ?? "").Trim(),
            CorrectGuidance = correctGuidance.Trim(),
            ContextSnippet = Truncate(contextSnippet ?? "", 4000),
            Tags = tags ?? "",
            CreatedAtUtc = DateTime.UtcNow,
            Enabled = true
        };

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.CorrectionLessons.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<IReadOnlyList<CorrectionLessonRecord>> ListAsync(
        bool enabledOnly = false, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = db.CorrectionLessons.AsNoTracking().AsQueryable();
        if (enabledOnly) q = q.Where(x => x.Enabled);
        return await q.OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
    }

    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.CorrectionLessons.FindAsync(new object[] { id }, ct);
        if (row is null) return;
        row.Enabled = enabled;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.CorrectionLessons.FindAsync(new object[] { id }, ct);
        if (row is null) return;
        db.CorrectionLessons.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> CountEnabledAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.CorrectionLessons.CountAsync(x => x.Enabled, ct);
    }

    /// <summary>Pick lessons for role + Any, newest first, bump UseCount.</summary>
    public async Task<IReadOnlyList<CorrectionLessonRecord>> PickForPromptAsync(
        AgentRole role, int maxN, CancellationToken ct = default)
    {
        if (maxN <= 0) return Array.Empty<CorrectionLessonRecord>();
        await InitializeAsync(ct);

        var roleName = role.ToString();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var picked = await db.CorrectionLessons
            .Where(x => x.Enabled && (x.Role == "Any" || x.Role == roleName))
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(maxN)
            .ToListAsync(ct);

        foreach (var p in picked)
            p.UseCount++;
        if (picked.Count > 0)
            await db.SaveChangesAsync(ct);

        return picked;
    }

    public static string FormatForPrompt(IReadOnlyList<CorrectionLessonRecord> lessons)
    {
        if (lessons.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Learned corrections (follow strictly)");
        foreach (var l in lessons)
        {
            sb.AppendLine($"- WRONG: {OneLine(l.WrongSummary)}");
            sb.AppendLine($"  RIGHT: {OneLine(l.CorrectGuidance)}");
            if (!string.IsNullOrWhiteSpace(l.ContextSnippet))
                sb.AppendLine($"  CONTEXT: {OneLine(Truncate(l.ContextSnippet, 400))}");
        }
        return sb.ToString().TrimEnd();
    }

    public static string FormatSystemBlock(IReadOnlyList<CorrectionLessonRecord> lessons)
    {
        if (lessons.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You must follow these learned corrections from prior user feedback:");
        foreach (var l in lessons)
        {
            sb.AppendLine($"- Do NOT: {OneLine(l.WrongSummary)}");
            sb.AppendLine($"  Instead: {OneLine(l.CorrectGuidance)}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    private static string OneLine(string s)
        => (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
}
