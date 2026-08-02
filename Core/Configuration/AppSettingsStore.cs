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
        public const string KeyLmStudioUrl = "LmStudioUrl";
        public const string KeyLmStudioDefaultUrl = "http://localhost:1234/v1";
        public const string KeyOnnxModelsPath = "OnnxModelsPath";
        public const string KeyDefaultAiProvider = "DefaultAiProvider";
        public const string KeyDefaultAiProviderDefault = "Ollama";

        public const string KeyTranslationEnabled = "TranslationEnabled";
        public const string KeyUserLanguage = "UserLanguage";      // "ru", "zh", "es", ...
        public const string KeyWorkingLanguage = "WorkingLanguage";   // язык, на котором работают агенты
        public const string KeyWorkingLanguageDefault = "English";

        public const string KeyArtistImageMode = "ArtistImageMode";
        public const string KeyArtistImageModeDefault = "Procedural";
        public const string KeyArtistOpenRouterImageModel = "ArtistOpenRouterImageModel";
        public const string KeyArtistOpenRouterImageModelDefault = "google/gemini-2.5-flash-image-preview";
        public const string KeyArtistOllamaImageModel = "ArtistOllamaImageModel";
        public const string KeyArtistOllamaImageModelDefault = "";

        /// <summary>Never | ImproveFix | Always — когда ждать Apply/Reject перед сборкой.</summary>
        public const string KeyReviewChangesMode = "ReviewChangesMode";
        public const string KeyReviewChangesModeDefault = "ImproveFix";

        public const string KeyExternalEditorPath = "ExternalEditorPath";
        public const string KeyExternalEditorArgs = "ExternalEditorArgs";
        public const string KeyExternalEditorArgsDefault = "\"{file}\"";

        public const string KeyFreelanceSkills = "FreelanceSkills";
        public const string KeyFreelanceSkillsDefault = "C#, .NET, WinUI, MonoGame, API, Windows Service";
        public const string KeyFreelanceHourlyRate = "FreelanceHourlyRate";
        public const string KeyFreelanceHourlyRateDefault = "30";
        public const string KeyFreelanceAutoAccept = "FreelanceAutoAccept";
        public const string KeyFreelanceAutoAcceptThreshold = "FreelanceAutoAcceptThreshold";
        public const string KeyFreelanceAutoAcceptThresholdDefault = "75";
        public const string KeyFreelanceMinProfit = "FreelanceMinProfit";
        public const string KeyFreelanceMinProfitDefault = "50";
        public const string KeyFreelanceEnableFlRu = "FreelanceEnableFlRu";
        public const string KeyFreelanceEnableKwork = "FreelanceEnableKwork";
        public const string KeyFreelanceEnableGitHub = "FreelanceEnableGitHub";
        public const string KeyFreelanceEnableDemo = "FreelanceEnableDemo";
        public const string KeyFreelanceKeywords = "FreelanceKeywords";
        public const string KeyFreelanceKeywordsDefault = "C# .NET WinUI";
        public const string KeyFreelanceGitHubQuery = "FreelanceGitHubQuery";
        public const string KeyFreelanceGitHubQueryDefault = "is:issue is:open label:bounty";
        public const string KeyFreelanceSimulationOnly = "FreelanceSimulationOnly";
        public const string KeyFreelanceLastHuntUtc = "FreelanceLastHuntUtc";
        public const string KeyFreelanceHuntMinIntervalSec = "FreelanceHuntMinIntervalSec";
        public const string KeyFreelanceHuntMinIntervalSecDefault = "30";


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

        public string GetLmStudioUrl() =>
            Get(KeyLmStudioUrl, KeyLmStudioDefaultUrl);

        public Task SetLmStudioUrlAsync(string url) =>
            SetAsync(KeyLmStudioUrl, url);

        public string GetOnnxModelsPath() =>
            Get(KeyOnnxModelsPath, PathHelper.ModelsRoot);

        public Task SetOnnxModelsPathAsync(string path) =>
            SetAsync(KeyOnnxModelsPath, path);

        public string GetDefaultAiProvider() =>
            Get(KeyDefaultAiProvider, KeyDefaultAiProviderDefault);

        public Task SetDefaultAiProviderAsync(string provider) =>
            SetAsync(KeyDefaultAiProvider, provider);

        public Core.Contracts.ArtistImageMode GetArtistImageMode()
        {
            var raw = Get(KeyArtistImageMode, KeyArtistImageModeDefault);
            return Enum.TryParse<Core.Contracts.ArtistImageMode>(raw, ignoreCase: true, out var m)
                ? m : Core.Contracts.ArtistImageMode.Procedural;
        }

        public Task SetArtistImageModeAsync(Core.Contracts.ArtistImageMode mode) =>
            SetAsync(KeyArtistImageMode, mode.ToString());

        public string GetArtistOpenRouterImageModel() =>
            Get(KeyArtistOpenRouterImageModel, KeyArtistOpenRouterImageModelDefault);

        public Task SetArtistOpenRouterImageModelAsync(string model) =>
            SetAsync(KeyArtistOpenRouterImageModel, model ?? "");

        public string GetArtistOllamaImageModel() =>
            Get(KeyArtistOllamaImageModel, KeyArtistOllamaImageModelDefault);

        public Task SetArtistOllamaImageModelAsync(string model) =>
            SetAsync(KeyArtistOllamaImageModel, model ?? "");

        public string GetReviewChangesMode() =>
            Get(KeyReviewChangesMode, KeyReviewChangesModeDefault);

        public Task SetReviewChangesModeAsync(string mode) =>
            SetAsync(KeyReviewChangesMode, mode ?? KeyReviewChangesModeDefault);

        public string GetExternalEditorPath() => Get(KeyExternalEditorPath, "");
        public string GetExternalEditorArgs() =>
            Get(KeyExternalEditorArgs, KeyExternalEditorArgsDefault);

        public Task SetExternalEditorPathAsync(string path) =>
            SetAsync(KeyExternalEditorPath, path ?? "");

        public Task SetExternalEditorArgsAsync(string args) =>
            SetAsync(KeyExternalEditorArgs, string.IsNullOrWhiteSpace(args)
                ? KeyExternalEditorArgsDefault
                : args);
    }
}
