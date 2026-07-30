using Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Configuration
{
    public sealed class AppSettingsStore
    {
        private readonly IDbContextFactory<AppDbContext> _dbFactory;
        private readonly Dictionary<string, string> _cache = new();
        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _loaded;

        public const string KeyOllamaUrl = "OllamaUrl";
        public const string KeyOllamaDefaultUrl = "http://localhost:11434";

        public event EventHandler<string>? SettingChanged;

        public AppSettingsStore(IDbContextFactory<AppDbContext> dbFactory) => _dbFactory = dbFactory;

        public async Task InitializeAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (_loaded) return;
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                await db.Database.EnsureCreatedAsync(ct);
                foreach (var r in await db.AppSettings.AsNoTracking().ToListAsync(ct))
                    _cache[r.Key] = r.Value;
                _loaded = true;
            }
            finally { _lock.Release(); }
        }

        public string Get(string key, string fallback = "") =>
            _cache.TryGetValue(key, out var v) ? v : fallback;

        public async Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                _cache[key] = value;
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var existing = await db.AppSettings.FindAsync(new object[] { key }, ct);
                if (existing is null) db.AppSettings.Add(new AppSettingRecord { Key = key, Value = value });
                else existing.Value = value;
                await db.SaveChangesAsync(ct);
            }
            finally { _lock.Release(); }
            SettingChanged?.Invoke(this, key);
        }
    }
}
