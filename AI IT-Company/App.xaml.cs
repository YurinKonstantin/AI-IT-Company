using Ai;
using Ai.Hosting;
using AI_IT_Company.ViewModels;
using Core;
using Core.Agents;
using Core.Configuration;
using Core.Contracts;
using Core.Hosting;
using Core.Orchestration;
using Core.Services;
using Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using Serilog;
using System;
using System.Threading.Tasks;
using ViewModels;

namespace AI_IT_Company
{
    public partial class App : Application
    {
        public static IHost Host { get; private set; } = null!;
        public Window? MainWindow;
        public static Window MainWindowRef => ((App)Current).MainWindow!;

        public App()
        {
            InitializeComponent();
            Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder().ConfigureServices((_, s) =>
            {
                s.AddLogging(b => b.AddSerilog(new LoggerConfiguration()
                    .WriteTo.File(
                        System.IO.Path.Combine(PathHelper.LogsRoot, "app-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 14).CreateLogger()));

                s.AddAiItProviders();
                s.AddAiItPipeline();

                // Image-gen для Artist (OpenRouter / Ollama); procedural — fallback внутри ArtistAgent.
                s.AddSingleton<IImageGenClient>(sp =>
                {
                    var settings = sp.GetRequiredService<AppSettingsStore>();
                    var creds = sp.GetRequiredService<WindowsCredentialStore>();
                    return new OpenRouterImageClient(
                        creds,
                        () => settings.GetOpenRouterBaseUrl());
                });
                s.AddSingleton<IImageGenClient>(sp =>
                {
                    var settings = sp.GetRequiredService<AppSettingsStore>();
                    return new OllamaImageClient(
                        () => settings.Get(AppSettingsStore.KeyOllamaUrl, AppSettingsStore.KeyOllamaDefaultUrl));
                });

                s.AddTransient<AgentsConfigViewModel>();
                s.AddTransient<ModelsViewModel>();
                s.AddTransient<SettingsViewModel>();
                s.AddTransient<ChangesViewModel>();
                s.AddTransient<AI_IT_Company.ViewModels.HelpViewModel>();

                s.AddSingleton<AI_IT_Company.Services.PipelineRunService>();
                s.AddSingleton<ChatViewModel>();
                s.AddSingleton<AI_IT_Company.ViewModels.DashboardViewModel>();
                s.AddSingleton<AI_IT_Company.ViewModels.LogsViewModel>();
                s.AddSingleton<AI_IT_Company.ViewModels.FreelanceViewModel>();
            }).Build();
        }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // Critical path only: settings + UI language, then show window immediately.
            var appSettings = Host.Services.GetRequiredService<AppSettingsStore>();
            await appSettings.InitializeAsync();

            var uiLang = appSettings.GetUiLanguage();
            if (!string.IsNullOrWhiteSpace(uiLang))
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = uiLang;

            AiProviderServiceCollectionExtensions.ApplyProviderUrlsFromSettings(Host.Services);

            MainWindow = new MainWindow();
            MainWindow.Activate();
            if (MainWindow is MainWindow mw)
                mw.SetStartupStatus("Loading services…");

            try
            {
                // Remaining stores — after first paint so the app does not look frozen.
                await Task.WhenAll(
                    Host.Services.GetRequiredService<AgentConfigStore>().InitializeAsync(),
                    Host.Services.GetRequiredService<AgentPromptStore>().InitializeAsync(),
                    Host.Services.GetRequiredService<SessionStore>().InitializeAsync(),
                    Host.Services.GetRequiredService<CorrectionLessonStore>().InitializeAsync(),
                    Host.Services.GetRequiredService<Core.Freelance.FreelanceJobStore>().InitializeAsync());

                await Host.Services.GetRequiredService<Core.Freelance.FreelanceStatsService>().EnsureSchemaAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Deferred store init failed: " + ex);
                if (MainWindow is MainWindow mwErr)
                    mwErr.SetStartupStatus("Startup error — see logs");
            }

            Host.Services
                .GetRequiredService<AI_IT_Company.Services.PipelineRunService>()
                .AttachUi(MainWindow.DispatcherQueue);

            if (MainWindow is MainWindow ready)
                ready.MarkServicesReady();
        }
    }
}
