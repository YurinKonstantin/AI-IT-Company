using Core.Agents;
using Core.Contracts;
using Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Configuration;

/// <summary>
/// Централизованный кэш+репозиторий настроек агентов.
/// - Читает при старте всё в память (быстрый доступ из pipeline).
/// - Пишет асинхронно в SQLite при сохранении из UI.
/// - Уведомляет подписчиков об изменениях (ConfigChanged).
/// </summary>
public sealed class AgentConfigStore
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<AgentConfigStore> _logger;
    private readonly Dictionary<AgentRole, AgentSettings> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _loaded;

    public event EventHandler<AgentRole>? ConfigChanged;

    public AgentConfigStore(
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<AgentConfigStore> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>Загрузить всё в память. Вызывать один раз при старте приложения.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_loaded) return;
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await db.Database.EnsureCreatedAsync(ct);

            var records = await db.AgentConfigs.AsNoTracking().ToListAsync(ct);
            foreach (var r in records)
            {
                if (Enum.TryParse<AgentRole>(r.Role, out var role))
                    _cache[role] = ToSettings(r);
            }

            // Заполняем дефолтами то, чего нет в БД.
            foreach (AgentRole role in Enum.GetValues<AgentRole>())
            {
                if (!_cache.ContainsKey(role))
                    _cache[role] = Default(role);
            }

            _loaded = true;
            _logger.LogInformation("AgentConfigStore инициализирован ({Count} записей)", _cache.Count);
        }
        finally { _lock.Release(); }
    }

    /// <summary>Синхронное получение настроек (быстро, из кэша).</summary>
    public AgentSettings Get(AgentRole role)
    {
        if (!_loaded)
            throw new InvalidOperationException("AgentConfigStore не инициализирован. Вызовите InitializeAsync().");
        return Clone(_cache[role]);
    }

    /// <summary>Все текущие настройки (для UI-страницы).</summary>
    public IReadOnlyDictionary<AgentRole, AgentSettings> GetAll()
        => _cache.ToDictionary(kv => kv.Key, kv => Clone(kv.Value));

    /// <summary>Сохранить настройки одного агента.</summary>
    public async Task SaveAsync(AgentRole role, AgentSettings settings, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _cache[role] = Clone(settings);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var existing = await db.AgentConfigs.FindAsync(new object[] { role.ToString() }, ct);
            if (existing is null)
            {
                db.AgentConfigs.Add(ToRecord(role, settings));
            }
            else
            {
                existing.Source = settings.Source;
                existing.ModelName = settings.ModelName;
                existing.TimeoutSeconds = settings.TimeoutSeconds;
                existing.MaxTokens = settings.MaxTokens;
                existing.Temperature = settings.Temperature;
                existing.ContextWindow = settings.ContextWindow;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Сохранены настройки агента {Role}", role);
        }
        finally { _lock.Release(); }

        ConfigChanged?.Invoke(this, role);
    }

    /// <summary>Сбросить настройки агента к дефолтным.</summary>
    public Task ResetAsync(AgentRole role, CancellationToken ct = default)
        => SaveAsync(role, Default(role), ct);

    // ---------- маппинг ----------
    private static AgentSettings ToSettings(AgentConfigRecord r) => new()
    {
        Source = r.Source,
        ModelName = r.ModelName,
        TimeoutSeconds = r.TimeoutSeconds,
        MaxTokens = r.MaxTokens,
        Temperature = r.Temperature,
        ContextWindow = r.ContextWindow
    };

    private static AgentConfigRecord ToRecord(AgentRole role, AgentSettings s) => new()
    {
        Role = role.ToString(),
        Source = s.Source,
        ModelName = s.ModelName,
        TimeoutSeconds = s.TimeoutSeconds,
        MaxTokens = s.MaxTokens,
        Temperature = s.Temperature,
        ContextWindow = s.ContextWindow,
        UpdatedAt = DateTime.UtcNow
    };

    private static AgentSettings Clone(AgentSettings s) => new()
    {
        Source = s.Source,
        ModelName = s.ModelName,
        TimeoutSeconds = s.TimeoutSeconds,
        MaxTokens = s.MaxTokens,
        Temperature = s.Temperature,
        ContextWindow = s.ContextWindow
    };

    /// <summary>Разумные дефолты под каждую роль.</summary>
    private static AgentSettings Default(AgentRole role) => role switch
    {
        // Интерпретатор — маленький, быстрый, детерминированный (JSON).
        AgentRole.Interpreter => new() { Source = "Ollama", ModelName = "qwen2.5-coder:7b", Temperature = 0.1 },

        // Переводчик — маленький, быстрый, детерминированный (JSON).
        AgentRole.Translator => new() { Source = "Ollama", ModelName = "qwen2.5-coder:7b", Temperature = 0.1 },

        // Архитектор — креативнее.
        AgentRole.Architect => new() { Source = "Ollama", ModelName = "qwen2.5-coder:7b", Temperature = 0.5 },

        // Кодеры — специализированные code-модели, низкая температура.
        AgentRole.BackendCoder => new() { Source = "Ollama", ModelName = "qwen2.5-coder:7b", Temperature = 0.2 },
        AgentRole.FrontendCoder => new() { Source = "Ollama", ModelName = "qwen2.5-coder:7b", Temperature = 0.2 },
        AgentRole.GameCoder => new() { Source = "Ollama", ModelName = "qwen2.5-coder:7b", Temperature = 0.3 },
        AgentRole.FullstackCoder => new() { Source = "Ollama", ModelName = "qwen2.5-coder:7b", Temperature = 0.3 },

        // Тестировщик — строгий.
        AgentRole.Tester => new() { Source = "Ollama", ModelName = "qwen2.5-coder:7b", Temperature = 0.15 },

        // Builder — LLM почти не нужна, ставим лёгкую.
        AgentRole.Builder => new() { Source = "Ollama", ModelName = "qwen2.5-coder:7b", Temperature = 0.0 },

        // Исправитель — вдумчивый.
        AgentRole.ErrorFixer => new() { Source = "Ollama", ModelName = "qwen2.5-coder:7b", Temperature = 0.1 },

        // Секретарь — тёплый текст.
        AgentRole.Secretary => new() { Source = "Ollama", ModelName = "qwen2.5-coder:7b", Temperature = 0.4 },

        _ => new()
    };
}