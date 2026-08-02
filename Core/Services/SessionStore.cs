using Core.Contracts;
using Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Services;

public sealed class SessionStore
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public SessionStore(IDbContextFactory<AppDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS Sessions (
                Id TEXT NOT NULL PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                Mode TEXT NOT NULL,
                Prompt TEXT NOT NULL,
                OutputRoot TEXT NOT NULL,
                Status TEXT NOT NULL,
                EventSummary TEXT NOT NULL DEFAULT '',
                StartedAt TEXT NOT NULL,
                FinishedAt TEXT NULL
            );
            """, ct);
    }

    public async Task StartAsync(AgentContext ctx, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Sessions.Add(new SessionRecord
        {
            Id = ctx.ProjectId,
            ProjectId = ctx.ProjectId,
            Mode = ctx.Mode.ToString(),
            Prompt = Truncate(ctx.SharedData.GetValueOrDefault("user_prompt_original", ctx.UserPrompt), 2000),
            OutputRoot = ctx.OutputRoot ?? ctx.ProjectPath ?? "",
            Status = "Running",
            EventSummary = "",
            StartedAt = DateTime.UtcNow
        });
        try { await db.SaveChangesAsync(ct); }
        catch { /* ignore duplicate */ }
    }

    public async Task FinishAsync(
        string sessionId,
        string status,
        string eventSummary,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Sessions.FindAsync(new object[] { sessionId }, ct);
        if (row is null) return;
        row.Status = status;
        row.EventSummary = Truncate(eventSummary, 4000);
        row.FinishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SessionRecord>> ListRecentAsync(int take = 30, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Sessions.AsNoTracking()
            .OrderByDescending(s => s.StartedAt)
            .Take(take)
            .ToListAsync(ct);
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}
