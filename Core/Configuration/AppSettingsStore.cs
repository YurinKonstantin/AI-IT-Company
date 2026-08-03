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

        /// <summary>UI BCP-47 tag: en-US | ru-RU | zh-Hans</summary>
        public const string KeyUiLanguage = "UiLanguage";
        public const string KeyUiLanguageDefault = "en-US";

        public const string KeyArtistImageMode = "ArtistImageMode";
        public const string KeyArtistImageModeDefault = "Procedural";
        public const string KeyArtistOpenRouterImageModel = "ArtistOpenRouterImageModel";
        public const string KeyArtistOpenRouterImageModelDefault = "google/gemini-2.5-flash-image-preview";
        public const string KeyArtistOllamaImageModel = "ArtistOllamaImageModel";
        public const string KeyArtistOllamaImageModelDefault = "";

        /// <summary>Never | ImproveFix | Always — когда ждать Apply/Reject перед сборкой.</summary>
        public const string KeyReviewChangesMode = "ReviewChangesMode";
        public const string KeyReviewChangesModeDefault = "ImproveFix";

        /// <summary>Studio | Autopilot — продуктовый режим оболочки.</summary>
        public const string KeyProductMode = "ProductMode";
        public const string KeyProductModeDefault = "Studio";

        /// <summary>Сколько раз ErrorFixer пробует исправить падение сборки на этапе.</summary>
        public const string KeyMaxBuildFixAttempts = "MaxBuildFixAttempts";
        public const int KeyMaxBuildFixAttemptsDefault = 3;

        /// <summary>true — спросить пользователя (ручное / далее с ошибкой / откат); false — сразу откат.</summary>
        public const string KeyAskUserOnBuildFixExhausted = "AskUserOnBuildFixExhausted";
        public const string KeyAskUserOnBuildFixExhaustedDefault = "true";

        public const string KeyCorrectionLessonsEnabled = "CorrectionLessonsEnabled";
        public const string KeyCorrectionLessonsEnabledDefault = "true";
        public const string KeyMaxCorrectionLessonsInPrompt = "MaxCorrectionLessonsInPrompt";
        public const int KeyMaxCorrectionLessonsInPromptDefault = 6;

        /// <summary>Устаревший ключ (миграция в AskUserOnBuildFixExhausted).</summary>
        public const string KeyBuildFixExhaustedAction = "BuildFixExhaustedAction";

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

        /// <summary>When true and PAT set: post a draft comment on GitHub issue Accept (never without this flag).</summary>
        public const string KeyFreelanceGitHubDraftComment = "FreelanceGitHubDraftComment";
        public const string KeyFreelanceGitHubDraftCommentDefault = "false";

        public const string KeyWebSearchEnabled = "WebSearchEnabled";
        public const string KeyWebSearchEnabledDefault = "false";
        public const string KeyWebSearchProvider = "WebSearchProvider";
        public const string KeyWebSearchProviderDefault = "Tavily";
        public const string KeyWebSearchMaxSnippets = "WebSearchMaxSnippets";
        public const int KeyWebSearchMaxSnippetsDefault = 3;
        public const string KeyWebSearchBudgetChars = "WebSearchBudgetChars";
        public const int KeyWebSearchBudgetCharsDefault = 1500;


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

        public string GetUiLanguage() => Get(KeyUiLanguage, KeyUiLanguageDefault);

        public Task SetUiLanguageAsync(string tag) =>
            SetAsync(KeyUiLanguage, string.IsNullOrWhiteSpace(tag) ? KeyUiLanguageDefault : tag.Trim());

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

        public string GetProductMode()
        {
            var v = Get(KeyProductMode, KeyProductModeDefault);
            return string.Equals(v, "Autopilot", StringComparison.OrdinalIgnoreCase)
                ? "Autopilot"
                : "Studio";
        }

        public bool IsAutopilotMode() =>
            string.Equals(GetProductMode(), "Autopilot", StringComparison.OrdinalIgnoreCase);

        public Task SetProductModeAsync(string mode) =>
            SetAsync(KeyProductMode,
                string.Equals(mode, "Autopilot", StringComparison.OrdinalIgnoreCase)
                    ? "Autopilot"
                    : "Studio");

        public int GetMaxBuildFixAttempts()
        {
            var raw = Get(KeyMaxBuildFixAttempts, KeyMaxBuildFixAttemptsDefault.ToString());
            if (!int.TryParse(raw, out var n)) return KeyMaxBuildFixAttemptsDefault;
            return Math.Clamp(n, 0, 20);
        }

        public Task SetMaxBuildFixAttemptsAsync(int attempts) =>
            SetAsync(KeyMaxBuildFixAttempts, Math.Clamp(attempts, 0, 20).ToString());

        public bool GetAskUserOnBuildFixExhausted()
        {
            if (_cache.ContainsKey(KeyAskUserOnBuildFixExhausted))
                return bool.TryParse(Get(KeyAskUserOnBuildFixExhausted, KeyAskUserOnBuildFixExhaustedDefault), out var b) && b;

            // Миграция со старого enum-ключа
            var legacy = Get(KeyBuildFixExhaustedAction, "");
            if (!string.IsNullOrWhiteSpace(legacy))
                return legacy.Equals("AskUser", StringComparison.OrdinalIgnoreCase);

            return bool.TryParse(KeyAskUserOnBuildFixExhaustedDefault, out var d) && d;
        }

        public Task SetAskUserOnBuildFixExhaustedAsync(bool ask) =>
            SetAsync(KeyAskUserOnBuildFixExhausted, ask.ToString());

        public bool GetCorrectionLessonsEnabled() =>
            !bool.TryParse(Get(KeyCorrectionLessonsEnabled, KeyCorrectionLessonsEnabledDefault), out var b) || b;

        public Task SetCorrectionLessonsEnabledAsync(bool enabled) =>
            SetAsync(KeyCorrectionLessonsEnabled, enabled.ToString());

        public int GetMaxCorrectionLessonsInPrompt()
        {
            var raw = Get(KeyMaxCorrectionLessonsInPrompt, KeyMaxCorrectionLessonsInPromptDefault.ToString());
            if (!int.TryParse(raw, out var n)) return KeyMaxCorrectionLessonsInPromptDefault;
            return Math.Clamp(n, 0, 30);
        }

        public Task SetMaxCorrectionLessonsInPromptAsync(int n) =>
            SetAsync(KeyMaxCorrectionLessonsInPrompt, Math.Clamp(n, 0, 30).ToString());

        public string GetExternalEditorPath() => Get(KeyExternalEditorPath, "");
        public string GetExternalEditorArgs() =>
            Get(KeyExternalEditorArgs, KeyExternalEditorArgsDefault);

        public Task SetExternalEditorPathAsync(string path) =>
            SetAsync(KeyExternalEditorPath, path ?? "");

        public Task SetExternalEditorArgsAsync(string args) =>
            SetAsync(KeyExternalEditorArgs, string.IsNullOrWhiteSpace(args)
                ? KeyExternalEditorArgsDefault
                : args);

        public bool GetFreelanceGitHubDraftComment() =>
            bool.TryParse(Get(KeyFreelanceGitHubDraftComment, KeyFreelanceGitHubDraftCommentDefault), out var b) && b;

        public Task SetFreelanceGitHubDraftCommentAsync(bool enabled) =>
            SetAsync(KeyFreelanceGitHubDraftComment, enabled.ToString());

        public bool GetWebSearchEnabled() =>
            bool.TryParse(Get(KeyWebSearchEnabled, KeyWebSearchEnabledDefault), out var b) && b;

        public Task SetWebSearchEnabledAsync(bool enabled) =>
            SetAsync(KeyWebSearchEnabled, enabled.ToString());

        public string GetWebSearchProvider()
        {
            var v = Get(KeyWebSearchProvider, KeyWebSearchProviderDefault);
            return string.Equals(v, "Brave", StringComparison.OrdinalIgnoreCase) ? "Brave" : "Tavily";
        }

        public Task SetWebSearchProviderAsync(string provider) =>
            SetAsync(KeyWebSearchProvider,
                string.Equals(provider, "Brave", StringComparison.OrdinalIgnoreCase) ? "Brave" : "Tavily");

        public int GetWebSearchMaxSnippets()
        {
            var raw = Get(KeyWebSearchMaxSnippets, KeyWebSearchMaxSnippetsDefault.ToString());
            if (!int.TryParse(raw, out var n)) return KeyWebSearchMaxSnippetsDefault;
            return Math.Clamp(n, 1, 3);
        }

        public Task SetWebSearchMaxSnippetsAsync(int n) =>
            SetAsync(KeyWebSearchMaxSnippets, Math.Clamp(n, 1, 3).ToString());

        public int GetWebSearchBudgetChars()
        {
            var raw = Get(KeyWebSearchBudgetChars, KeyWebSearchBudgetCharsDefault.ToString());
            if (!int.TryParse(raw, out var n)) return KeyWebSearchBudgetCharsDefault;
            return Math.Clamp(n, 400, 4000);
        }

        public Task SetWebSearchBudgetCharsAsync(int n) =>
            SetAsync(KeyWebSearchBudgetChars, Math.Clamp(n, 400, 4000).ToString());
    }
}
