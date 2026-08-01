using Core;
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
        public const string KeyCoderMode = "CoderMode";
        public const string KeyCoderModeDefault = "Unified";

        public const string KeyOpenRouterBaseUrl = "OpenRouterBaseUrl";
        public const string KeyOpenRouterDefaultUrl = "https://openrouter.ai/api/v1";
        public const string KeyOnnxModelsPath = "OnnxModelsPath";
        public const string KeyDefaultAiProvider = "DefaultAiProvider";
        public const string KeyDefaultAiProviderDefault = "Ollama";

        public const string KeyTranslationEnabled = "TranslationEnabled";
        public const string KeyUserLanguage = "UserLanguage";      // "ru", "zh", "es", ...
        public const string KeyWorkingLanguage = "WorkingLanguage";   // язык, на котором работают агенты
        public const string KeyWorkingLanguageDefault = "English";

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

        public Core.Contracts.CoderMode GetCoderMode()
        {
            var raw = Get(KeyCoderMode, KeyCoderModeDefault);
            return System.Enum.TryParse<Core.Contracts.CoderMode>(raw, ignoreCase: true, out var m)
                ? m : Core.Contracts.CoderMode.Split;
        }

        public Task SetCoderModeAsync(Core.Contracts.CoderMode mode) => SetAsync(KeyCoderMode, mode.ToString());




        public bool GetTranslationEnabled() =>
    bool.TryParse(Get(KeyTranslationEnabled, "true"), out var b) && b;

        public Task SetTranslationEnabledAsync(bool v) =>
            SetAsync(KeyTranslationEnabled, v.ToString());

        public string GetUserLanguage() => Get(KeyUserLanguage, "Russian");
        public string GetWorkingLanguage() => Get(KeyWorkingLanguage, KeyWorkingLanguageDefault);

        public Task SetUserLanguageAsync(string v) => SetAsync(KeyUserLanguage, v);
        public Task SetWorkingLanguageAsync(string v) => SetAsync(KeyWorkingLanguage, v);

        public string GetOpenRouterBaseUrl() =>
            Get(KeyOpenRouterBaseUrl, KeyOpenRouterDefaultUrl);

        public Task SetOpenRouterBaseUrlAsync(string url) =>
            SetAsync(KeyOpenRouterBaseUrl, url);

        public string GetOnnxModelsPath() =>
            Get(KeyOnnxModelsPath, PathHelper.ModelsRoot);

        public Task SetOnnxModelsPathAsync(string path) =>
            SetAsync(KeyOnnxModelsPath, path);

        public string GetDefaultAiProvider() =>
            Get(KeyDefaultAiProvider, KeyDefaultAiProviderDefault);

        public Task SetDefaultAiProviderAsync(string provider) =>
            SetAsync(KeyDefaultAiProvider, provider);
    }
}
