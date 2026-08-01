using Core.Configuration;
using Core.Contracts;
using Core.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents;

public abstract class AgentBase : IAgent
{
    private readonly IAiProviderFactory _factory;
    private readonly AgentConfigStore _configStore;
    private readonly AgentPromptStore _promptStore;
    protected readonly ILogger Logger;

    public abstract AgentRole Role { get; }
    public virtual string Name => Role.ToString();
    ITranslationService _translator;

    public event EventHandler<string>? OnStream;

    protected AgentBase(
     IAiProviderFactory factory,
     AgentConfigStore configStore,
     AgentPromptStore promptStore, ITranslationService translation,
     ILogger logger)
    {
        _factory = factory;
        _configStore = configStore;
        _promptStore = promptStore;
        _translator = translation;
        Logger = logger;
    }
    protected AgentBase(
    IAiProviderFactory factory,
    AgentConfigStore configStore,
    AgentPromptStore promptStore,
    ILogger logger)
    {
        _factory = factory;
        _configStore = configStore;
        _promptStore = promptStore;
        Logger = logger;
    }
    /// <summary>Дефолтный промпт из кода — реализация в наследнике.</summary>
    protected abstract string DefaultSystemPrompt { get; }

    /// <summary>Итоговый промпт: пользовательский, если задан, иначе дефолтный.</summary>
    protected string SystemPrompt => _promptStore.Get(Role) ?? DefaultSystemPrompt;
    private static string GetDefaultOutputRoot(string projectId)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiItCompany", "Output", projectId);
        return root;
    }
    public virtual async Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
    {
        try
        {
            var settings = _configStore.Get(Role);
            Debug.WriteLine("Agent " + Name + " " + "Model" + settings.ModelName);

            var provider = _factory.Resolve(settings.Source);
            Debug.WriteLine("Agent " + Name + " provider " + provider.Name + " " + "Model" + settings.ModelName);

            var outputRoot = ctx.ProjectPath ?? GetDefaultOutputRoot(ctx.ProjectId);
            Directory.CreateDirectory(outputRoot);
            // ... остальное без изменений ...
            ctx.SharedData["output_root"] = outputRoot;   // ← сохраним, чтоб Секретарь и UI увидели
            Logger.LogInformation("[{Agent}] Старт (provider={Provider}, model={Model})",
                Name, provider.Name, settings.ModelName);

            var userPrompt = BuildUserPrompt(ctx);
            var sb = new StringBuilder();
            var req = new AiRequest(
                ModelName: settings.ModelName,
                SystemPrompt: GetSystemPromptForRun(ct),
                UserPrompt: userPrompt,
                Temperature: settings.Temperature,
                MaxTokens: settings.MaxTokens,
                ContextWindow: settings.ContextWindow,
                Timeout: settings.TimeoutSeconds == 0
                                     ? null
                                     : TimeSpan.FromSeconds(settings.TimeoutSeconds));

            await foreach (var chunk in provider.GenerateStreamAsync(req, ct))
            {
                sb.Append(chunk);
                OnStream?.Invoke(this, chunk);
            }
            var text = sb.ToString();
            Logger.LogInformation("[{Agent}] Готово ({Len} симв.)", Name, text.Length);
            return await PostProcessAsync(ctx, text, ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[{Agent}] Ошибка", Name);
            return new AgentResult(false, "", ex.Message);
        }
    }

    protected abstract string BuildUserPrompt(AgentContext ctx);

    protected virtual Task<AgentResult> PostProcessAsync(AgentContext ctx, string output, CancellationToken ct)
        => Task.FromResult(new AgentResult(true, output));
    protected string GetSystemPromptForRun(CancellationToken ct)
    {
        var custom = _promptStore.Get(Role);
        if (custom is null) return DefaultSystemPrompt;                // уже английский

        // Есть кастомный — переводим на рабочий язык.
        // Синхронный wrapper над async — оправдан только внутри выполнения агента (не UI-thread).
        return _translator.ToWorkingAsync(custom, ct).GetAwaiter().GetResult();
    }
}