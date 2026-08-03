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

    /// <summary>
    /// Применить пресет моделей ко всем ролям.
    /// compact — слабый/средний ПК (7b); balanced — 14b на ключевых ролях.
    /// </summary>
    public async Task ApplyPresetAsync(string profile, CancellationToken ct = default)
    {
        foreach (AgentRole role in Enum.GetValues<AgentRole>())
        {
            await SaveAsync(role, Preset(profile, role), ct);
        }
        _logger.LogInformation("Применён пресет моделей: {Profile}", profile);
    }

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

    private static AgentSettings Preset(string profile, AgentRole role)
        => string.Equals(profile, "balanced", StringComparison.OrdinalIgnoreCase)
            ? Balanced(role)
            : Default(role);

    /// <summary>Short UI hint: role tier + recommended models for weak PCs.</summary>
    public static string GetRecommendedHint(AgentRole role) => role switch
    {
        AgentRole.ErrorFixer =>
            "Fixer · Compact: qwen2.5-coder:7b (ctx 8k, out 3k) · Balanced: 14b. Prefer instruction-following coder; low temperature.",
        AgentRole.BackendCoder or AgentRole.FrontendCoder or AgentRole.FullstackCoder
            or AgentRole.GameCoder or AgentRole.Tester =>
            "Coder · Compact: qwen2.5-coder:7b (ctx 8k) · Balanced: 14b. Keep MaxTokens ≤4k on weak GPU.",
        AgentRole.Architect =>
            "Architect · Compact: qwen2.5-coder:7b · Balanced: 14b. Larger ctx (12k) for plans.",
        AgentRole.Documenter or AgentRole.Analyst or AgentRole.UxReviewer =>
            "Docs · Compact: llama3.2:3b (ctx 4k, out 2k) · Balanced: llama3.1:8b. Small context avoids Ollama 500.",
        AgentRole.Secretary or AgentRole.Translator or AgentRole.Interpreter =>
            "Light · Compact: 7b/3b text · short answers (ctx 4k).",
        AgentRole.Artist =>
            "Artist · coder 7b/14b for manifests; images via Settings → Artist.",
        AgentRole.Builder or AgentRole.Scaffolder =>
            "No LLM required — local tools only (model ignored).",
        _ => "Ollama local · keep ContextWindow ≤8192 on weak PCs (hard cap 16384)."
    };

    /// <summary>Разумные дефолты под каждую роль (compact / слабый ПК).</summary>
    private static AgentSettings Default(AgentRole role) => role switch
    {
        // Light / routing
        AgentRole.Interpreter => S("qwen2.5-coder:7b", 0.1, ctx: 4_096, maxOut: 1_024),
        AgentRole.Translator => S("qwen2.5-coder:7b", 0.1, ctx: 4_096, maxOut: 2_048),
        AgentRole.Secretary => S("llama3.2:3b", 0.4, ctx: 4_096, maxOut: 2_048),

        // Architect — slightly larger ctx for plans
        AgentRole.Architect => S("qwen2.5-coder:7b", 0.5, ctx: 12_288, maxOut: 4_096),

        // Coders
        AgentRole.BackendCoder => S("qwen2.5-coder:7b", 0.2, ctx: 8_192, maxOut: 4_096),
        AgentRole.FrontendCoder => S("qwen2.5-coder:7b", 0.2, ctx: 8_192, maxOut: 4_096),
        AgentRole.GameCoder => S("qwen2.5-coder:7b", 0.3, ctx: 8_192, maxOut: 4_096),
        AgentRole.FullstackCoder => S("qwen2.5-coder:7b", 0.3, ctx: 8_192, maxOut: 4_096),
        AgentRole.Tester => S("qwen2.5-coder:7b", 0.15, ctx: 8_192, maxOut: 3_072),
        AgentRole.Artist => S("qwen2.5-coder:7b", 0.45, ctx: 6_144, maxOut: 2_048),

        // Fixer — instruction-following, moderate out
        AgentRole.ErrorFixer => S("qwen2.5-coder:7b", 0.1, ctx: 8_192, maxOut: 3_072),

        // Docs — small models + tight budgets (Ollama OOM prevention)
        AgentRole.Documenter => S("llama3.2:3b", 0.35, ctx: 4_096, maxOut: 2_048),
        AgentRole.Analyst => S("llama3.2:3b", 0.35, ctx: 4_096, maxOut: 2_048),
        AgentRole.UxReviewer => S("llama3.2:3b", 0.35, ctx: 4_096, maxOut: 2_048),

        // Non-LLM
        AgentRole.Builder => S("qwen2.5-coder:7b", 0.0, ctx: 2_048, maxOut: 256),
        AgentRole.Scaffolder => S("qwen2.5-coder:7b", 0.0, ctx: 2_048, maxOut: 256),

        _ => S("qwen2.5-coder:7b", 0.2, ctx: 8_192, maxOut: 4_096)
    };

    /// <summary>Пресет balanced (≈16–24 GB): 14b на кодерах/фиксере/архитекторе.</summary>
    private static AgentSettings Balanced(AgentRole role) => role switch
    {
        AgentRole.Interpreter => S("qwen2.5-coder:7b", 0.1, ctx: 4_096, maxOut: 1_024),
        AgentRole.Translator => S("qwen2.5-coder:7b", 0.1, ctx: 4_096, maxOut: 2_048),
        AgentRole.Secretary => S("llama3.1:8b", 0.4, ctx: 6_144, maxOut: 3_072),

        AgentRole.Architect => S("qwen2.5-coder:14b", 0.5, ctx: 16_384, maxOut: 6_144),

        AgentRole.BackendCoder => S("qwen2.5-coder:14b", 0.2, ctx: 12_288, maxOut: 6_144),
        AgentRole.FrontendCoder => S("qwen2.5-coder:14b", 0.2, ctx: 12_288, maxOut: 6_144),
        AgentRole.GameCoder => S("qwen2.5-coder:14b", 0.3, ctx: 12_288, maxOut: 6_144),
        AgentRole.FullstackCoder => S("qwen2.5-coder:14b", 0.3, ctx: 12_288, maxOut: 6_144),
        AgentRole.Tester => S("qwen2.5-coder:14b", 0.15, ctx: 12_288, maxOut: 4_096),
        AgentRole.Artist => S("qwen2.5-coder:14b", 0.45, ctx: 8_192, maxOut: 3_072),

        AgentRole.ErrorFixer => S("qwen2.5-coder:14b", 0.1, ctx: 12_288, maxOut: 4_096),

        AgentRole.Documenter => S("llama3.1:8b", 0.35, ctx: 8_192, maxOut: 4_096),
        AgentRole.Analyst => S("llama3.1:8b", 0.35, ctx: 8_192, maxOut: 4_096),
        AgentRole.UxReviewer => S("llama3.1:8b", 0.35, ctx: 8_192, maxOut: 4_096),

        AgentRole.Builder => S("qwen2.5-coder:7b", 0.0, ctx: 2_048, maxOut: 256),
        AgentRole.Scaffolder => S("qwen2.5-coder:7b", 0.0, ctx: 2_048, maxOut: 256),

        _ => S("qwen2.5-coder:14b", 0.2, ctx: 12_288, maxOut: 4_096)
    };

    private static AgentSettings S(string model, double temperature, int ctx = 8_192, int maxOut = 4_096) => new()
    {
        Source = "Ollama",
        ModelName = model,
        Temperature = temperature,
        ContextWindow = ctx,
        MaxTokens = maxOut
    };
}