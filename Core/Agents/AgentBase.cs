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
    private readonly CorrectionLessonStore? _lessons;
    private readonly AppSettingsStore? _appSettings;
    private readonly IWebSearchService? _webSearch;
    protected readonly ILogger Logger;

    public abstract AgentRole Role { get; }
    public virtual string Name => Role.ToString();
    ITranslationService? _translator;

    public event EventHandler<string>? OnStream;

    protected AgentBase(
     IAiProviderFactory factory,
     AgentConfigStore configStore,
     AgentPromptStore promptStore, ITranslationService translation,
     ILogger logger,
     CorrectionLessonStore? lessons = null,
     AppSettingsStore? appSettings = null,
     IWebSearchService? webSearch = null)
    {
        _factory = factory;
        _configStore = configStore;
        _promptStore = promptStore;
        _translator = translation;
        Logger = logger;
        _lessons = lessons;
        _appSettings = appSettings;
        _webSearch = webSearch;
    }

    protected AgentBase(
    IAiProviderFactory factory,
    AgentConfigStore configStore,
    AgentPromptStore promptStore,
    ILogger logger,
    CorrectionLessonStore? lessons = null,
    AppSettingsStore? appSettings = null,
    IWebSearchService? webSearch = null)
    {
        _factory = factory;
        _configStore = configStore;
        _promptStore = promptStore;
        Logger = logger;
        _lessons = lessons;
        _appSettings = appSettings;
        _webSearch = webSearch;
    }

    protected abstract string DefaultSystemPrompt { get; }

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
            ctx.SharedData["output_root"] = outputRoot;
            var modelTag = $"{provider.Name}/{settings.ModelName}";
            ctx.SharedData["last_agent_model"] = modelTag;
            Logger.LogInformation("[{Agent}] Старт (provider={Provider}, model={Model})",
                Name, provider.Name, settings.ModelName);

            var userPrompt = BuildUserPrompt(ctx);
            var lessonBlock = await BuildLessonBlockAsync(ct);
            if (!string.IsNullOrWhiteSpace(lessonBlock))
                userPrompt = lessonBlock + "\n\n" + userPrompt;

            var webBlock = await BuildWebSearchBlockAsync(ctx, ct);
            if (!string.IsNullOrWhiteSpace(webBlock))
                userPrompt = webBlock + "\n\n" + userPrompt;

            var systemPrompt = await GetSystemPromptForRunAsync(ct);
            var sb = new StringBuilder();
            var req = new AiRequest(
                ModelName: settings.ModelName,
                SystemPrompt: systemPrompt,
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
            var model = "";
            try
            {
                var s = _configStore.Get(Role);
                model = s.Source + "/" + s.ModelName;
            }
            catch { /* store may be unavailable */ }
            var msg = string.IsNullOrEmpty(model) ? ex.Message : $"[{model}] {ex.Message}";
            return new AgentResult(false, "", msg);
        }
    }

    protected abstract string BuildUserPrompt(AgentContext ctx);

    protected virtual Task<AgentResult> PostProcessAsync(AgentContext ctx, string output, CancellationToken ct)
        => Task.FromResult(new AgentResult(true, output));

    protected async Task<string> GetSystemPromptForRunAsync(CancellationToken ct)
    {
        var custom = _promptStore.Get(Role);
        if (custom is null) return DefaultSystemPrompt;

        if (_translator is null) return custom;
        try
        {
            return await _translator.ToWorkingAsync(custom, ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[{Agent}] System prompt translate failed — using original", Name);
            return custom;
        }
    }

    private static bool RoleUsesLessons(AgentRole role) => role is
        AgentRole.ErrorFixer or AgentRole.BackendCoder or AgentRole.FrontendCoder
        or AgentRole.FullstackCoder or AgentRole.GameCoder or AgentRole.Architect;

    private static bool RoleUsesWebSearch(AgentRole role) => role is
        AgentRole.Architect or AgentRole.ErrorFixer;

    private async Task<string> BuildWebSearchBlockAsync(AgentContext ctx, CancellationToken ct)
    {
        if (_webSearch is null || !RoleUsesWebSearch(Role)) return "";
        var query = ctx.UserPrompt;
        if (Role == AgentRole.ErrorFixer)
        {
            ctx.SharedData.TryGetValue("build_log", out var errors);
            if (!string.IsNullOrWhiteSpace(errors))
                query = TruncateForSearch(errors!, 500) + "\n" + ctx.UserPrompt;
        }

        return await _webSearch.BuildPromptBlockAsync(query, ct);
    }

    private static string TruncateForSearch(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[^max..];

    private async Task<string> BuildLessonBlockAsync(CancellationToken ct)
    {
        if (_lessons is null || _appSettings is null) return "";
        if (!_appSettings.GetCorrectionLessonsEnabled()) return "";
        if (!RoleUsesLessons(Role)) return "";

        var max = _appSettings.GetMaxCorrectionLessonsInPrompt();
        if (max <= 0) return "";

        var picked = await _lessons.PickForPromptAsync(Role, max, ct);
        return CorrectionLessonStore.FormatForPrompt(picked);
    }
}
