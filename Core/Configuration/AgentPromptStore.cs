using Core.Contracts;
using Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Configuration
{
    /// <summary>
    /// Хранилище пользовательских системных промптов агентов.
    /// Если для роли пользователь ничего не сохранил — возвращается null,
    /// и агент использует свой дефолтный промпт (SystemPrompt в коде).
    /// </summary>
    public sealed class AgentPromptStore
    {
        private readonly IDbContextFactory<AppDbContext> _dbFactory;
        private readonly Dictionary<AgentRole, string> _custom = new();
        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _loaded;

        public event EventHandler<AgentRole>? PromptChanged;

        public AgentPromptStore(IDbContextFactory<AppDbContext> dbFactory) => _dbFactory = dbFactory;

        public async Task InitializeAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (_loaded) return;
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                await db.Database.EnsureCreatedAsync(ct);
                foreach (var r in await db.Set<AgentPromptRecord>().AsNoTracking().ToListAsync(ct))
                    if (Enum.TryParse<AgentRole>(r.Role, out var role) && !string.IsNullOrWhiteSpace(r.Prompt))
                        _custom[role] = r.Prompt;
                _loaded = true;
            }
            finally { _lock.Release(); }
        }

        /// <summary>Пользовательский промпт (null → использовать дефолт из кода).</summary>
        public string? Get(AgentRole role) => _custom.TryGetValue(role, out var p) ? p : null;

        public async Task SaveAsync(AgentRole role, string prompt, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                _custom[role] = prompt;
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var existing = await db.Set<AgentPromptRecord>().FindAsync(new object[] { role.ToString() }, ct);
                if (existing is null)
                    db.Set<AgentPromptRecord>().Add(new AgentPromptRecord
                    {
                        Role = role.ToString(),
                        Prompt = prompt,
                        UpdatedAt = DateTime.UtcNow
                    });
                else
                {
                    existing.Prompt = prompt;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
                await db.SaveChangesAsync(ct);
            }
            finally { _lock.Release(); }
            PromptChanged?.Invoke(this, role);
        }

        public async Task ResetAsync(AgentRole role, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                _custom.Remove(role);
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var existing = await db.Set<AgentPromptRecord>().FindAsync(new object[] { role.ToString() }, ct);
                if (existing is not null)
                {
                    db.Remove(existing);
                    await db.SaveChangesAsync(ct);
                }
            }
            finally { _lock.Release(); }
            PromptChanged?.Invoke(this, role);
        }
    }
}
