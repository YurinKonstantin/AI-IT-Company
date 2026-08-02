using Core.Agents;
using Core.Configuration;
using Core.Contracts;
using Core.Freelance;
using Core.Orchestration;
using Core.Services;
using Data;
using Microsoft.Extensions.DependencyInjection;

namespace Core.Hosting;

/// <summary>
/// Общая регистрация агентов и пайплайна для WinUI-приложения и CLI.
/// </summary>
public static class PipelineServiceCollectionExtensions
{
    public static IServiceCollection AddAiItPipeline(this IServiceCollection s)
    {
        s.AddDbContextFactory<AppDbContext>();
        s.AddSingleton<AppSettingsStore>();
        s.AddSingleton<AgentConfigStore>();
        s.AddSingleton<AgentPromptStore>();
        s.AddSingleton<PendingChangeService>();
        s.AddSingleton<SessionStore>();
        s.AddSingleton<FreelanceJobStore>();
        s.AddSingleton<FreelanceStatsService>();
        s.AddSingleton<FreelanceOpportunityScorer>();
        s.AddSingleton<FreelanceHuntService>();

        s.AddTransient<IAgent, InterpreterAgent>();
        s.AddTransient<IAgent, ArchitectAgent>();
        s.AddTransient<IAgent, BackendCoderAgent>();
        s.AddTransient<IAgent, FrontendCoderAgent>();
        s.AddTransient<IAgent, FullstackCoderAgent>();
        s.AddTransient<IAgent, GameCoderAgent>();
        s.AddTransient<IAgent, ArtistAgent>();
        s.AddTransient<IAgent, TesterAgent>();
        s.AddTransient<IAgent, BuilderAgent>();
        s.AddTransient<IAgent, ErrorFixerAgent>();
        s.AddTransient<IAgent, SecretaryAgent>();
        s.AddTransient<IAgent, ScaffolderAgent>();
        s.AddTransient<IAgent, DocumenterAgent>();
        s.AddTransient<IAgent, AnalystAgent>();
        s.AddTransient<IAgent, UxReviewerAgent>();

        s.AddSingleton<TranslatorAgent>();
        s.AddTransient<IAgent>(sp => sp.GetRequiredService<TranslatorAgent>());
        s.AddSingleton<ITranslationService, TranslationService>();

        s.AddTransient<StagedPipeline>();
        return s;
    }
}
