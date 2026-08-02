using Ai;
using Build;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Configuration;
using Core.Contracts;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;
using AppRoot = AI_IT_Company.App;

namespace AI_IT_Company.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly AppSettingsStore _settings;
        private readonly OllamaClient _client;
        private readonly OllamaProvider _ollamaProvider;
        private readonly OpenRouterProvider _openRouterProvider;
        private readonly LmStudioProvider _lmStudioProvider;
        private readonly OnnxProvider _onnxProvider;
        private readonly WindowsCredentialStore _credentials;
        private readonly AgentPromptStore _promptStore;
        private readonly DispatcherQueue _ui;

        public string[] CoderModeOptions { get; } =
        {
            "🧑‍🤝‍🧑 Два кодера: Backend + Frontend (последовательно)",
            "🧑‍💻 Один универсальный Fullstack-кодер"
        };

        public string[] AiProviderOptions { get; } = { "Ollama", "OpenRouter", "LM Studio", "ONNX" };

        [ObservableProperty] private bool translationEnabled = true;
        [ObservableProperty] private string userLanguage = "Russian";
        [ObservableProperty] private string workingLanguage = "English";
        [ObservableProperty] private string translationStatus = "";

        public string[] SupportedLanguages { get; } =
            { "English", "Russian", "Chinese", "Spanish", "German", "French",
      "Portuguese", "Japanese", "Korean", "Italian", "Ukrainian", "Polish" };

        [RelayCommand]
        private async Task SaveTranslationAsync()
        {
            await _settings.SetTranslationEnabledAsync(TranslationEnabled);
            await _settings.SetUserLanguageAsync(UserLanguage);
            await _settings.SetWorkingLanguageAsync(WorkingLanguage);
            TranslationStatus = TranslationEnabled
                ? $"✅ {UserLanguage} ↔ {WorkingLanguage}"
                : "⏸ Перевод отключён";
        }

        [ObservableProperty] private int coderModeIndex;
        [ObservableProperty] private string coderModeStatus = "";

        // --- Artist image-gen ---
        public string[] ArtistImageModeOptions { get; } =
        {
            "🎨 Procedural (локально, без API)",
            "☁ OpenRouter (image API)",
            "🦙 Ollama (локальная image-модель)"
        };

        [ObservableProperty] private int artistImageModeIndex;
        [ObservableProperty] private string artistOpenRouterImageModel = "";
        [ObservableProperty] private string artistOllamaImageModel = "";
        [ObservableProperty] private string artistImageStatus = "";

        // --- Default provider ---
        [ObservableProperty] private string defaultAiProvider = "Ollama";
        [ObservableProperty] private string defaultProviderStatus = "";

        // --- Ollama ---
        [ObservableProperty] private string ollamaUrl = "";
        [ObservableProperty] private string ollamaStatus = "";
        [ObservableProperty] private bool isOllamaOk;

        // --- OpenRouter ---
        [ObservableProperty] private string openRouterBaseUrl = "";
        /// <summary>Черновик ключа из PasswordBox (не сохраняется в SQLite).</summary>
        [ObservableProperty] private string openRouterApiKeyInput = "";
        [ObservableProperty] private bool hasOpenRouterApiKey;
        [ObservableProperty] private string openRouterKeyHint = "";
        [ObservableProperty] private string openRouterStatus = "";
        [ObservableProperty] private bool isOpenRouterOk;

        // --- LM Studio ---
        [ObservableProperty] private string lmStudioUrl = "";
        [ObservableProperty] private string lmStudioStatus = "";
        [ObservableProperty] private bool isLmStudioOk;

        // --- ONNX ---
        [ObservableProperty] private string onnxModelsPath = "";
        [ObservableProperty] private string onnxStatus = "";
        [ObservableProperty] private bool isOnnxOk;
        public ObservableCollection<string> OnnxModelFolders { get; } = new();

        // --- Шаблоны ---
        public ObservableCollection<TemplateStatusItem> Templates { get; } = new();
        [ObservableProperty] private bool isTemplatesLoading;
        [ObservableProperty] private string templatesStatus = "";

        // --- Промпты ---
        public ObservableCollection<PromptItem> Prompts { get; } = new();

        // --- Cursor-like: review + editor ---
        public string[] ReviewChangesModeOptions { get; } =
        {
            "ImproveFix — ревью только Improve/Fix",
            "Always — всегда ждать Apply",
            "Never — авто-применение"
        };

        [ObservableProperty] private int reviewChangesModeIndex;
        [ObservableProperty] private string reviewChangesStatus = "";
        [ObservableProperty] private string externalEditorPath = "";
        [ObservableProperty] private string externalEditorArgs = "";
        [ObservableProperty] private string externalEditorStatus = "";

        // --- Freelance ---
        [ObservableProperty] private string freelanceSkills = "";
        [ObservableProperty] private string freelanceHourlyRate = "30";
        [ObservableProperty] private string freelanceKeywords = "";
        [ObservableProperty] private string freelanceGitHubQuery = "";
        [ObservableProperty] private bool freelanceEnableDemo = true;
        [ObservableProperty] private bool freelanceEnableGitHub = true;
        [ObservableProperty] private bool freelanceEnableFlRu;
        [ObservableProperty] private bool freelanceEnableKwork;
        [ObservableProperty] private bool freelanceAutoAccept;
        [ObservableProperty] private bool freelanceSimulationOnly;
        [ObservableProperty] private string freelanceAutoAcceptThreshold = "75";
        [ObservableProperty] private string freelanceMinProfit = "50";
        [ObservableProperty] private string freelanceFlRuFeedUrl = "";
        [ObservableProperty] private string freelanceKworkFeedUrl = "";
        [ObservableProperty] private string gitHubPatInput = "";
        [ObservableProperty] private bool hasGitHubPat;
        [ObservableProperty] private string freelanceStatus = "";

        public SettingsViewModel(
            AppSettingsStore settings,
            OllamaClient client,
            OllamaProvider ollamaProvider,
            OpenRouterProvider openRouterProvider,
            LmStudioProvider lmStudioProvider,
            OnnxProvider onnxProvider,
            WindowsCredentialStore credentials,
            AgentPromptStore promptStore)
        {
            _settings = settings;
            _client = client;
            _ollamaProvider = ollamaProvider;
            _openRouterProvider = openRouterProvider;
            _lmStudioProvider = lmStudioProvider;
            _onnxProvider = onnxProvider;
            _credentials = credentials;
            _promptStore = promptStore;
            _ui = DispatcherQueue.GetForCurrentThread()
                  ?? throw new InvalidOperationException("UI-поток нужен.");

            OllamaUrl = _settings.Get(AppSettingsStore.KeyOllamaUrl, AppSettingsStore.KeyOllamaDefaultUrl);
            OpenRouterBaseUrl = _settings.GetOpenRouterBaseUrl();
            LmStudioUrl = _settings.GetLmStudioUrl();
            OnnxModelsPath = _settings.GetOnnxModelsPath();
            DefaultAiProvider = _settings.GetDefaultAiProvider();
            HasOpenRouterApiKey = _credentials.HasSecret(WindowsCredentialStore.OpenRouterResource);
            UpdateOpenRouterKeyHint();

            CoderModeIndex = _settings.GetCoderMode() == Core.Contracts.CoderMode.Unified ? 1 : 0;
            ArtistImageModeIndex = _settings.GetArtistImageMode() switch
            {
                ArtistImageMode.OpenRouter => 1,
                ArtistImageMode.Ollama => 2,
                _ => 0
            };
            ArtistOpenRouterImageModel = _settings.GetArtistOpenRouterImageModel();
            ArtistOllamaImageModel = _settings.GetArtistOllamaImageModel();
            TranslationEnabled = _settings.GetTranslationEnabled();
            UserLanguage = _settings.GetUserLanguage();
            WorkingLanguage = _settings.GetWorkingLanguage();
            ReviewChangesModeIndex = _settings.GetReviewChangesMode().Trim().ToLowerInvariant() switch
            {
                "always" => 1,
                "never" or "off" => 2,
                _ => 0
            };
            ExternalEditorPath = _settings.GetExternalEditorPath();
            ExternalEditorArgs = _settings.GetExternalEditorArgs();
            FreelanceSkills = _settings.Get(AppSettingsStore.KeyFreelanceSkills, AppSettingsStore.KeyFreelanceSkillsDefault);
            FreelanceHourlyRate = _settings.Get(AppSettingsStore.KeyFreelanceHourlyRate, AppSettingsStore.KeyFreelanceHourlyRateDefault);
            FreelanceKeywords = _settings.Get(AppSettingsStore.KeyFreelanceKeywords, AppSettingsStore.KeyFreelanceKeywordsDefault);
            FreelanceGitHubQuery = _settings.Get(AppSettingsStore.KeyFreelanceGitHubQuery, AppSettingsStore.KeyFreelanceGitHubQueryDefault);
            FreelanceEnableDemo = bool.TryParse(_settings.Get(AppSettingsStore.KeyFreelanceEnableDemo, "true"), out var d) && d;
            FreelanceEnableGitHub = bool.TryParse(_settings.Get(AppSettingsStore.KeyFreelanceEnableGitHub, "true"), out var g) && g;
            FreelanceEnableFlRu = bool.TryParse(_settings.Get(AppSettingsStore.KeyFreelanceEnableFlRu, "false"), out var f) && f;
            FreelanceEnableKwork = bool.TryParse(_settings.Get(AppSettingsStore.KeyFreelanceEnableKwork, "false"), out var k) && k;
            FreelanceAutoAccept = bool.TryParse(_settings.Get(AppSettingsStore.KeyFreelanceAutoAccept, "false"), out var a) && a;
            FreelanceSimulationOnly = bool.TryParse(_settings.Get(AppSettingsStore.KeyFreelanceSimulationOnly, "false"), out var sim) && sim;
            FreelanceAutoAcceptThreshold = _settings.Get(AppSettingsStore.KeyFreelanceAutoAcceptThreshold, AppSettingsStore.KeyFreelanceAutoAcceptThresholdDefault);
            FreelanceMinProfit = _settings.Get(AppSettingsStore.KeyFreelanceMinProfit, AppSettingsStore.KeyFreelanceMinProfitDefault);
            FreelanceFlRuFeedUrl = _settings.Get(Ai.Freelance.FlRuMarketplaceAdapter.FeedUrlKey, "");
            FreelanceKworkFeedUrl = _settings.Get(Ai.Freelance.KworkMarketplaceAdapter.FeedUrlKey, "");
            HasGitHubPat = _credentials.HasSecret(WindowsCredentialStore.GitHubPatResource);
            LoadTemplates();
            LoadPrompts();
            RefreshOnnxFolderList();
            _ = RefreshTemplatesAsync();
            _ = CheckOllamaAsync();
            _ = CheckOpenRouterAsync();
            _ = CheckLmStudioAsync();
            _ = CheckOnnxAsync();
        }

        [RelayCommand]
        private async Task SaveFreelanceAsync()
        {
            await _settings.SetAsync(AppSettingsStore.KeyFreelanceSkills, FreelanceSkills ?? "");
            await _settings.SetAsync(AppSettingsStore.KeyFreelanceHourlyRate, FreelanceHourlyRate ?? "30");
            await _settings.SetAsync(AppSettingsStore.KeyFreelanceKeywords, FreelanceKeywords ?? "");
            await _settings.SetAsync(AppSettingsStore.KeyFreelanceGitHubQuery, FreelanceGitHubQuery ?? "");
            await _settings.SetAsync(AppSettingsStore.KeyFreelanceEnableDemo, FreelanceEnableDemo.ToString());
            await _settings.SetAsync(AppSettingsStore.KeyFreelanceEnableGitHub, FreelanceEnableGitHub.ToString());
            await _settings.SetAsync(AppSettingsStore.KeyFreelanceEnableFlRu, FreelanceEnableFlRu.ToString());
            await _settings.SetAsync(AppSettingsStore.KeyFreelanceEnableKwork, FreelanceEnableKwork.ToString());
            await _settings.SetAsync(AppSettingsStore.KeyFreelanceAutoAccept, FreelanceAutoAccept.ToString());
            await _settings.SetAsync(AppSettingsStore.KeyFreelanceSimulationOnly, FreelanceSimulationOnly.ToString());
            await _settings.SetAsync(AppSettingsStore.KeyFreelanceAutoAcceptThreshold, FreelanceAutoAcceptThreshold ?? "75");
            await _settings.SetAsync(AppSettingsStore.KeyFreelanceMinProfit, FreelanceMinProfit ?? "50");
            await _settings.SetAsync(Ai.Freelance.FlRuMarketplaceAdapter.FeedUrlKey, FreelanceFlRuFeedUrl ?? "");
            await _settings.SetAsync(Ai.Freelance.KworkMarketplaceAdapter.FeedUrlKey, FreelanceKworkFeedUrl ?? "");

            if (!string.IsNullOrWhiteSpace(GitHubPatInput))
            {
                _credentials.SetSecret(WindowsCredentialStore.GitHubPatResource, GitHubPatInput.Trim());
                GitHubPatInput = "";
                HasGitHubPat = true;
            }

            FreelanceStatus = "💾 Настройки биржи сохранены"
                + (HasGitHubPat ? " · GitHub PAT есть" : " · GitHub без PAT (лимит API ниже)");
        }

        [RelayCommand]
        private void ClearGitHubPat()
        {
            _credentials.RemoveSecret(WindowsCredentialStore.GitHubPatResource);
            HasGitHubPat = false;
            FreelanceStatus = "GitHub PAT удалён";
        }

        [RelayCommand]
        private async Task SaveReviewChangesAsync()
        {
            var mode = ReviewChangesModeIndex switch
            {
                1 => "Always",
                2 => "Never",
                _ => "ImproveFix"
            };
            await _settings.SetReviewChangesModeAsync(mode);
            ReviewChangesStatus = $"💾 Ревью изменений: {mode}";
        }

        [RelayCommand]
        private async Task SaveExternalEditorAsync()
        {
            await _settings.SetExternalEditorPathAsync((ExternalEditorPath ?? "").Trim());
            await _settings.SetExternalEditorArgsAsync((ExternalEditorArgs ?? "").Trim());
            ExternalEditorStatus = string.IsNullOrWhiteSpace(ExternalEditorPath)
                ? "💾 Редактор не задан — будет explorer /select"
                : $"💾 Редактор: {ExternalEditorPath}";
        }

        [RelayCommand]
        private async Task SaveArtistImageAsync()
        {
            var mode = ArtistImageModeIndex switch
            {
                1 => ArtistImageMode.OpenRouter,
                2 => ArtistImageMode.Ollama,
                _ => ArtistImageMode.Procedural
            };
            await _settings.SetArtistImageModeAsync(mode);
            await _settings.SetArtistOpenRouterImageModelAsync(
                (ArtistOpenRouterImageModel ?? "").Trim());
            await _settings.SetArtistOllamaImageModelAsync(
                (ArtistOllamaImageModel ?? "").Trim());

            ArtistImageStatus = mode switch
            {
                ArtistImageMode.OpenRouter =>
                    $"💾 OpenRouter → {(string.IsNullOrWhiteSpace(ArtistOpenRouterImageModel) ? AppSettingsStore.KeyArtistOpenRouterImageModelDefault : ArtistOpenRouterImageModel)}",
                ArtistImageMode.Ollama =>
                    string.IsNullOrWhiteSpace(ArtistOllamaImageModel)
                        ? "⚠ Режим Ollama, но модель не указана — будет procedural fallback"
                        : $"💾 Ollama → {ArtistOllamaImageModel}",
                _ => "💾 Procedural (локально)"
            };
        }

        // ---------- DEFAULT PROVIDER ----------
        [RelayCommand]
        private async Task SaveDefaultProviderAsync()
        {
            var p = string.IsNullOrWhiteSpace(DefaultAiProvider) ? "Ollama" : DefaultAiProvider.Trim();
            await _settings.SetDefaultAiProviderAsync(p);
            DefaultProviderStatus = $"💾 По умолчанию: {p}";
        }

        // ---------- OLLAMA ----------
        [RelayCommand]
        private async Task SaveOllamaAsync()
        {
            var url = (OllamaUrl ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                OllamaStatus = "❌ Некорректный URL";
                return;
            }
            await _settings.SetAsync(AppSettingsStore.KeyOllamaUrl, url);
            _client.SetBaseUrl(url);
            _ollamaProvider.SetBaseUrl(url);
            OllamaStatus = "💾 Сохранено, проверяем…";
            await CheckOllamaAsync();
        }

        [RelayCommand]
        private async Task CheckOllamaAsync()
        {
            IsOllamaOk = await _ollamaProvider.IsAvailableAsync();
            OllamaStatus = IsOllamaOk
                ? $"✅ Соединение с {_ollamaProvider.BaseUrl} работает."
                : $"❌ Не удалось подключиться к {_ollamaProvider.BaseUrl}.";
        }

        // ---------- LM STUDIO ----------
        [RelayCommand]
        private async Task SaveLmStudioAsync()
        {
            var url = (LmStudioUrl ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                LmStudioStatus = "❌ Некорректный URL";
                return;
            }
            await _settings.SetLmStudioUrlAsync(url);
            _lmStudioProvider.SetBaseUrl(url);
            LmStudioStatus = "💾 Сохранено, проверяем…";
            await CheckLmStudioAsync();
        }

        [RelayCommand]
        private async Task CheckLmStudioAsync()
        {
            IsLmStudioOk = await _lmStudioProvider.IsAvailableAsync();
            LmStudioStatus = IsLmStudioOk
                ? $"✅ Соединение с {_lmStudioProvider.BaseUrl} работает."
                : $"❌ Не удалось подключиться к {_lmStudioProvider.BaseUrl}. Запустите Local Server в LM Studio.";
        }

        // ---------- OPENROUTER ----------
        [RelayCommand]
        private async Task SaveOpenRouterAsync()
        {
            var url = (OpenRouterBaseUrl ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                OpenRouterStatus = "❌ Некорректный Base URL";
                return;
            }

            await _settings.SetOpenRouterBaseUrlAsync(url);
            _openRouterProvider.SetBaseUrl(url);

            if (!string.IsNullOrWhiteSpace(OpenRouterApiKeyInput))
            {
                _credentials.SetSecret(WindowsCredentialStore.OpenRouterResource, OpenRouterApiKeyInput);
                OpenRouterApiKeyInput = "";
                HasOpenRouterApiKey = true;
                UpdateOpenRouterKeyHint();
            }

            OpenRouterStatus = "💾 Сохранено, проверяем…";
            await CheckOpenRouterAsync();
        }

        [RelayCommand]
        private async Task CheckOpenRouterAsync()
        {
            HasOpenRouterApiKey = _credentials.HasSecret(WindowsCredentialStore.OpenRouterResource);
            UpdateOpenRouterKeyHint();
            if (!HasOpenRouterApiKey)
            {
                IsOpenRouterOk = false;
                OpenRouterStatus = "⚠ API key не задан (хранится в Windows Credential Locker).";
                return;
            }

            IsOpenRouterOk = await _openRouterProvider.IsAvailableAsync();
            OpenRouterStatus = IsOpenRouterOk
                ? $"✅ OpenRouter доступен ({_openRouterProvider.BaseUrl})."
                : "❌ Не удалось подключиться к OpenRouter (проверьте ключ и сеть).";
        }

        [RelayCommand]
        private void ClearOpenRouterKey()
        {
            _credentials.RemoveSecret(WindowsCredentialStore.OpenRouterResource);
            HasOpenRouterApiKey = false;
            IsOpenRouterOk = false;
            OpenRouterApiKeyInput = "";
            UpdateOpenRouterKeyHint();
            OpenRouterStatus = "🗑 API key удалён из Credential Locker.";
        }

        private void UpdateOpenRouterKeyHint()
        {
            OpenRouterKeyHint = HasOpenRouterApiKey
                ? "Ключ сохранён в Windows Credential Locker. Введите новый, чтобы заменить."
                : "Ключ ещё не сохранён. Вставьте API key и нажмите «Сохранить».";
        }

        // ---------- ONNX ----------
        [RelayCommand]
        private async Task SaveOnnxAsync()
        {
            var path = (OnnxModelsPath ?? "").Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                OnnxStatus = "❌ Укажите папку моделей";
                return;
            }

            try
            {
                System.IO.Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                OnnxStatus = "❌ " + ex.Message;
                return;
            }

            await _settings.SetOnnxModelsPathAsync(path);
            _onnxProvider.SetModelsRoot(path);
            RefreshOnnxFolderList();
            OnnxStatus = "💾 Сохранено, проверяем…";
            await CheckOnnxAsync();
        }

        [RelayCommand]
        private async Task BrowseOnnxFolderAsync()
        {
            try
            {
                var picker = new FolderPicker();
                picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
                picker.FileTypeFilter.Add("*");

                var hwnd = WindowNative.GetWindowHandle(AppRoot.MainWindowRef);
                InitializeWithWindow.Initialize(picker, hwnd);

                var folder = await picker.PickSingleFolderAsync();
                if (folder is null) return;

                OnnxModelsPath = folder.Path;
                await SaveOnnxAsync();
            }
            catch (Exception ex)
            {
                OnnxStatus = "❌ " + ex.Message;
            }
        }

        [RelayCommand]
        private async Task CheckOnnxAsync()
        {
            IsOnnxOk = await _onnxProvider.IsAvailableAsync();
            RefreshOnnxFolderList();
            if (!IsOnnxOk)
            {
                OnnxStatus = $"❌ Папка не найдена: {_onnxProvider.ModelsRoot}";
                return;
            }

            OnnxStatus = OnnxModelFolders.Count == 0
                ? $"✅ Папка OK ({_onnxProvider.ModelsRoot}). Подпапок моделей пока нет — положите GenAI-модель вручную."
                : $"✅ Папка OK. Найдено моделей: {OnnxModelFolders.Count} ({string.Join(", ", OnnxModelFolders.Take(5))}{(OnnxModelFolders.Count > 5 ? "…" : "")}).";
        }

        private void RefreshOnnxFolderList()
        {
            OnnxModelFolders.Clear();
            foreach (var name in _onnxProvider.ListLocalModelFolders())
                OnnxModelFolders.Add(name);
        }

        // ---------- TEMPLATES ----------
        private void LoadTemplates()
        {
            Templates.Clear();
            foreach (var t in TemplateRegistry.All)
                Templates.Add(new TemplateStatusItem
                {
                    Key = t.Key,
                    DisplayName = t.DisplayName,
                    DotnetNewName = t.DotnetNewName,
                    InstallPackage = t.InstallPackage,
                    IsBuiltin = t.IsBuiltin,
                    Status = "проверка…"
                });
        }

        [RelayCommand]
        private async Task RefreshTemplatesAsync()
        {
            IsTemplatesLoading = true;
            try
            {
                var installed = await DotnetTemplateService.ListInstalledShortNamesAsync();
                foreach (var it in Templates)
                {
                    it.IsInstalled = installed.Contains(it.DotnetNewName);
                    it.Status = it.IsInstalled
                        ? "✅ установлен"
                        : (it.IsBuiltin ? "⚠ ожидается в SDK" : "❌ не установлен");
                }
                TemplatesStatus = $"Проверено {Templates.Count} шаблонов.";
            }
            catch (Exception ex) { TemplatesStatus = "❌ " + ex.Message; }
            finally { IsTemplatesLoading = false; }
        }

        [RelayCommand]
        private async Task InstallTemplateAsync(TemplateStatusItem it)
        {
            if (it is null) return;
            if (string.IsNullOrEmpty(it.InstallPackage))
            {
                it.Status = "встроенный (обновите .NET SDK)";
                return;
            }
            it.Status = "устанавливаем…";
            var (ok, log) = await DotnetTemplateService.InstallPackageAsync(it.InstallPackage);
            it.Status = ok ? "✅ установлен" : "❌ ошибка";
            if (!ok) TemplatesStatus = log;
            await RefreshTemplatesAsync();
        }

        // ---------- PROMPTS ----------
        private void LoadPrompts()
        {
            Prompts.Clear();
            foreach (AgentRole role in Enum.GetValues<AgentRole>())
            {
                var custom = _promptStore.Get(role);
                Prompts.Add(new PromptItem
                {
                    Role = role,
                    Title = RoleTitle(role),
                    Text = custom ?? "",
                    IsCustom = custom is not null,
                    Placeholder = "Оставьте пустым — будет использован дефолтный системный промпт из кода."
                });
            }
        }

        [RelayCommand]
        private async Task SavePromptAsync(PromptItem item)
        {
            if (item is null) return;
            var text = (item.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                await _promptStore.ResetAsync(item.Role);
                item.IsCustom = false;
                item.Status = "↺ Возврат к дефолту";
            }
            else
            {
                await _promptStore.SaveAsync(item.Role, text);
                item.IsCustom = true;
                item.Status = $"💾 Сохранено {DateTime.Now:HH:mm:ss}";
            }
        }

        [RelayCommand]
        private async Task ResetPromptAsync(PromptItem item)
        {
            if (item is null) return;
            await _promptStore.ResetAsync(item.Role);
            item.Text = "";
            item.IsCustom = false;
            item.Status = "↺ Сброшено";
        }

        private static string RoleTitle(AgentRole r) => r switch
        {
            AgentRole.Interpreter => "🧭 Интерпретатор",
            AgentRole.Architect => "🏛 Архитектор",
            AgentRole.BackendCoder => "⚙ Backend",
            AgentRole.FrontendCoder => "🎨 Frontend",
            AgentRole.FullstackCoder => "🧑‍💻 Fullstack-Кодер",
            AgentRole.GameCoder => "🎮 Game",
            AgentRole.Tester => "🧪 Тестировщик",
            AgentRole.Builder => "🔧 Сборщик",
            AgentRole.ErrorFixer => "🩹 Исправитель",
            AgentRole.Secretary => "📝 Секретарь",
            AgentRole.Scaffolder => "🏗 Скаффолдер",
            AgentRole.Translator => "🌐 Переводчик",
            AgentRole.Documenter => "📚 Документатор",
            AgentRole.Artist => "🎨 Художник",
            AgentRole.Analyst => "🔍 Аналитик",
            _ => r.ToString()
        };

        [RelayCommand]
        private async Task SaveCoderModeAsync()
        {
            var mode = CoderModeIndex == 1
                ? Core.Contracts.CoderMode.Unified
                : Core.Contracts.CoderMode.Split;

            await _settings.SetCoderModeAsync(mode);
            CoderModeStatus = $"💾 Сохранено: {CoderModeOptions[CoderModeIndex]}";
        }
    }

    public partial class TemplateStatusItem : ObservableObject
    {
        public string Key { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string DotnetNewName { get; init; } = "";
        public string InstallPackage { get; init; } = "";
        public bool IsBuiltin { get; init; }

        [ObservableProperty] private bool isInstalled;
        [ObservableProperty] private string status = "";
    }

    public partial class PromptItem : ObservableObject
    {
        public AgentRole Role { get; init; }
        public string Title { get; init; } = "";
        public string Placeholder { get; init; } = "";
        [ObservableProperty] private string text = "";
        [ObservableProperty] private bool isCustom;
        [ObservableProperty] private string status = "";
    }
}
