using Core.Agents;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Configuration;
using Core.Contracts;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace ViewModels;

public partial class AgentConfigItem : ObservableObject
{
    public AgentRole Role { get; }
    public string DisplayName { get; }
    public string RecommendedHint { get; }
    public string RoleTier { get; }

    [ObservableProperty] private string source = "Ollama";
    [ObservableProperty] private string modelName = "";
    [ObservableProperty] private int timeoutSeconds;
    [ObservableProperty] private int maxTokens;
    [ObservableProperty] private double temperature;
    [ObservableProperty] private int contextWindow;

    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private string statusText = "";

    public IReadOnlyList<string> AvailableSources { get; } = new[] { "Ollama", "OpenRouter", "LM Studio", "ONNX" };

    public AgentConfigItem(AgentRole role, AgentSettings s)
    {
        Role = role;
        DisplayName = RoleToRussian(role);
        RoleTier = RoleToTier(role);
        RecommendedHint = AgentConfigStore.GetRecommendedHint(role);
        Apply(s);
        // Помечаем dirty при любых изменениях.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsDirty) or nameof(StatusText)) return;
            IsDirty = true;
        };
    }

    public void Apply(AgentSettings s)
    {
        Source = s.Source;
        ModelName = s.ModelName;
        TimeoutSeconds = s.TimeoutSeconds;
        MaxTokens = s.MaxTokens;
        Temperature = s.Temperature;
        ContextWindow = s.ContextWindow;
        IsDirty = false;
    }

    public AgentSettings ToSettings() => new()
    {
        Source = Source,
        ModelName = ModelName,
        TimeoutSeconds = TimeoutSeconds,
        MaxTokens = MaxTokens,
        Temperature = Temperature,
        ContextWindow = ContextWindow
    };

    private static string RoleToTier(AgentRole r) => r switch
    {
        AgentRole.ErrorFixer => "Fixer",
        AgentRole.BackendCoder or AgentRole.FrontendCoder or AgentRole.FullstackCoder
            or AgentRole.GameCoder or AgentRole.Tester => "Coder",
        AgentRole.Architect => "Architect",
        AgentRole.Documenter or AgentRole.Analyst or AgentRole.UxReviewer => "Docs",
        AgentRole.Builder or AgentRole.Scaffolder => "Tools",
        _ => "Light"
    };

    private static string RoleToRussian(AgentRole r) => r switch
    {
        AgentRole.Interpreter => "🧭 Интерпретатор",
        AgentRole.Architect => "🏛 Архитектор",
        AgentRole.BackendCoder => "⚙ Кодер (Backend)",
        AgentRole.FrontendCoder => "🎨 Кодер (Frontend / WinUI)",
        AgentRole.FullstackCoder => "🧑‍💻 Fullstack-Кодер (Backend+Frontend)",
        AgentRole.GameCoder => "🎮 Кодер (Monogame)",
        AgentRole.Tester => "🧪 Тестировщик",
        AgentRole.Builder => "🔧 Сборщик (DevOps)",
        AgentRole.ErrorFixer => "🩹 Исправитель ошибок",
        AgentRole.Secretary => "📝 Секретарь",
        AgentRole.Scaffolder => "🏗 Скаффолдер",
        AgentRole.Translator => "🌐 Переводчик",
        AgentRole.Documenter => "📚 Документатор",
        AgentRole.Artist => "🎨 Художник (спрайты)",
        AgentRole.Analyst => "🔍 Аналитик",
        AgentRole.UxReviewer => "🖥 UX-ревьюер",
        _ => r.ToString()
    };
}

public partial class AgentsConfigViewModel : ObservableObject
{
    private readonly AgentConfigStore _store;

    public ObservableCollection<AgentConfigItem> Agents { get; } = new();

    [ObservableProperty] private string globalStatus = "";

    public AgentsConfigViewModel(AgentConfigStore store)
    {
        _store = store;
        Reload();
    }

    private void Reload()
    {
        Agents.Clear();
        foreach (var (role, settings) in _store.GetAll())
            Agents.Add(new AgentConfigItem(role, settings));
    }

    [RelayCommand]
    private async Task SaveAsync(AgentConfigItem item)
    {
        try
        {
            await _store.SaveAsync(item.Role, item.ToSettings());
            item.IsDirty = false;
            item.StatusText = $"✅ Сохранено {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            item.StatusText = $"❌ Ошибка: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ResetAsync(AgentConfigItem item)
    {
        await _store.ResetAsync(item.Role);
        var fresh = _store.Get(item.Role);
        item.Apply(fresh);
        item.StatusText = "↺ Сброшено к дефолту";
    }

    [RelayCommand]
    private async Task SaveAllAsync()
    {
        int saved = 0;
        foreach (var item in Agents.Where(a => a.IsDirty).ToList())
        {
            await _store.SaveAsync(item.Role, item.ToSettings());
            item.IsDirty = false;
            saved++;
        }
        GlobalStatus = $"✅ Сохранено агентов: {saved}";
    }

    [RelayCommand]
    private async Task ApplyCompactPresetAsync()
    {
        await _store.ApplyPresetAsync("compact");
        Reload();
        GlobalStatus = "✅ Пресет Compact (Fixer/Coder/Docs · 7b/3b, ужатый ctx) применён";
    }

    [RelayCommand]
    private async Task ApplyBalancedPresetAsync()
    {
        await _store.ApplyPresetAsync("balanced");
        Reload();
        GlobalStatus = "✅ Пресет Balanced (14b кодеры/fixer, 8b docs) применён";
    }
}