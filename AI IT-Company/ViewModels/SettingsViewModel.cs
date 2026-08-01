using Ai;
using Build;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Configuration;
using Core.Contracts;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading.Tasks;
using System.Transactions;

namespace AI_IT_Company.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly AppSettingsStore _settings;
        private readonly OllamaClient _client;
        private readonly AgentPromptStore _promptStore;
        private readonly DispatcherQueue _ui;

        public string[] CoderModeOptions { get; } =
{
    "🧑‍🤝‍🧑 Два кодера: Backend + Frontend (последовательно)",
    "🧑‍💻 Один универсальный Fullstack-кодер"
};


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



        // В поля класса:
        [ObservableProperty] private int coderModeIndex;  // 0 = Split, 1 = Unified
        [ObservableProperty] private string coderModeStatus = "";

        // --- Ollama ---
        [ObservableProperty] private string ollamaUrl = "";
        [ObservableProperty] private string ollamaStatus = "";
        [ObservableProperty] private bool isOllamaOk;

        // --- Шаблоны ---
        public ObservableCollection<TemplateStatusItem> Templates { get; } = new();
        [ObservableProperty] private bool isTemplatesLoading;
        [ObservableProperty] private string templatesStatus = "";

        // --- Промпты ---
        public ObservableCollection<PromptItem> Prompts { get; } = new();

        public SettingsViewModel(
            AppSettingsStore settings,
            OllamaClient client,
            AgentPromptStore promptStore)
        {
            _settings = settings;
            _client = client;
            _promptStore = promptStore;
            _ui = DispatcherQueue.GetForCurrentThread()
                  ?? throw new InvalidOperationException("UI-поток нужен.");

            OllamaUrl = _settings.Get(AppSettingsStore.KeyOllamaUrl, AppSettingsStore.KeyOllamaDefaultUrl);
            CoderModeIndex = _settings.GetCoderMode() == Core.Contracts.CoderMode.Unified ? 1 : 0;
            TranslationEnabled = _settings.GetTranslationEnabled();
            UserLanguage = _settings.GetUserLanguage();
            WorkingLanguage = _settings.GetWorkingLanguage();
            LoadTemplates();
            LoadPrompts();
            _ = RefreshTemplatesAsync();
            _ = CheckOllamaAsync();
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
            OllamaStatus = "💾 Сохранено, проверяем…";
            await CheckOllamaAsync();
        }

        [RelayCommand]
        private async Task CheckOllamaAsync()
        {
            IsOllamaOk = await _client.PingAsync();
            OllamaStatus = IsOllamaOk
                ? $"✅ Соединение с {_client.BaseUrl} работает."
                : $"❌ Не удалось подключиться к {_client.BaseUrl}.";
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

        // ---------- ROLE TITLES ----------
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
