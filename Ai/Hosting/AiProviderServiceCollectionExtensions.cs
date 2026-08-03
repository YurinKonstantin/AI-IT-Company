using Ai;
using Ai.Freelance;
using Core;
using Core.Configuration;
using Core.Contracts;
using Core.Freelance;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Ai.Hosting;

public static class AiProviderServiceCollectionExtensions
{
    public static IServiceCollection AddAiItProviders(this IServiceCollection s)
    {
        s.AddSingleton<WindowsCredentialStore>();
        s.AddSingleton<OllamaProvider>(_ => new OllamaProvider("http://localhost:11434"));
        s.AddSingleton<OpenRouterProvider>(sp =>
            new OpenRouterProvider(
                sp.GetRequiredService<WindowsCredentialStore>(),
                "https://openrouter.ai/api/v1"));
        s.AddSingleton<LmStudioProvider>(_ => new LmStudioProvider("http://localhost:1234/v1"));
        s.AddSingleton<OnnxProvider>(_ => new OnnxProvider(PathHelper.ModelsRoot));
        s.AddSingleton<IAiProviderFactory, AiProviderFactory>();
        s.AddSingleton<OllamaClient>(_ => new OllamaClient("http://localhost:11434"));

        // Freelance marketplace adapters
        s.AddSingleton<IFreelanceMarketplace, DemoMarketplaceAdapter>();
        s.AddSingleton<GitHubBountyMarketplaceAdapter>();
        s.AddSingleton<IFreelanceMarketplace>(sp => sp.GetRequiredService<GitHubBountyMarketplaceAdapter>());
        s.AddSingleton<IFreelanceMarketplace, FlRuMarketplaceAdapter>();
        s.AddSingleton<IFreelanceMarketplace, KworkMarketplaceAdapter>();
        s.AddSingleton<IWebSearchService, WebSearchService>();
        return s;
    }

    public static void ApplyProviderUrlsFromSettings(IServiceProvider sp)
    {
        var settings = sp.GetRequiredService<AppSettingsStore>();
        var url = settings.Get(AppSettingsStore.KeyOllamaUrl, AppSettingsStore.KeyOllamaDefaultUrl);
        sp.GetRequiredService<OllamaClient>().SetBaseUrl(url);
        sp.GetRequiredService<OllamaProvider>().SetBaseUrl(url);
        sp.GetRequiredService<OpenRouterProvider>().SetBaseUrl(settings.GetOpenRouterBaseUrl());
        sp.GetRequiredService<LmStudioProvider>().SetBaseUrl(settings.GetLmStudioUrl());
        sp.GetRequiredService<OnnxProvider>().SetModelsRoot(settings.GetOnnxModelsPath());
    }
}
