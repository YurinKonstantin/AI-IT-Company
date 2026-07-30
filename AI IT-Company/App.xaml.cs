using Ai;
using AI_IT_Company.ViewModels;
using Core;
using Core.Agents;
using Core.Configuration;
using Core.Contracts;
using Core.Orchestration;
using Data;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using ViewModels;
using ViewModels;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace AI_IT_Company
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        public static IHost Host { get; private set; } = null!;
        public Window? MainWindow;

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
                // БД
                s.AddDbContextFactory<AppDbContext>();

                // Провайдеры AI
                s.AddSingleton<OllamaProvider>(_ => new OllamaProvider("http://localhost:11434"));
                //s.AddSingleton<OnnxProvider>(_ => new OnnxProvider(
                // System.IO.Path.Combine(AppContext.BaseDirectory, "Models")));

                // Фабрика — через ИНТЕРФЕЙС
                s.AddSingleton<IAiProviderFactory, AiProviderFactory>();

                // Настройки агентов
                s.AddSingleton<AgentConfigStore>();

                // Агенты через IAgent
                s.AddTransient<IAgent, InterpreterAgent>();
                s.AddTransient<IAgent, ArchitectAgent>();
                s.AddTransient<IAgent, BackendCoderAgent>();
                s.AddTransient<IAgent, FrontendCoderAgent>();
                s.AddTransient<IAgent, GameCoderAgent>();
                s.AddTransient<IAgent, TesterAgent>();
                s.AddTransient<IAgent, BuilderAgent>();
                s.AddTransient<IAgent, ErrorFixerAgent>();
                s.AddTransient<IAgent, SecretaryAgent>();

                // s.AddTransient<AgentPipeline>();
                s.AddTransient<StagedPipeline>();
                s.AddTransient<AgentsConfigViewModel>();
                s.AddTransient<ChatViewModel>();
                s.AddSingleton<OllamaClient>(_ => new OllamaClient("http://localhost:11434"));
                s.AddTransient<ModelsViewModel>();
                s.AddTransient<IAgent, ScaffolderAgent>();
                s.AddSingleton<AppSettingsStore>();
                s.AddSingleton<AgentPromptStore>();
                s.AddTransient<SettingsViewModel>();

            }).Build();
        }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            var appSettings = Host.Services.GetRequiredService<AppSettingsStore>();
            await appSettings.InitializeAsync();

            var url = appSettings.Get(AppSettingsStore.KeyOllamaUrl,
                                      AppSettingsStore.KeyOllamaDefaultUrl);
            Host.Services.GetRequiredService<OllamaClient>().SetBaseUrl(url);
            // (Опционально) обновите OllamaProvider тем же URL, если он не читает у клиента.

            await Host.Services.GetRequiredService<AgentConfigStore>().InitializeAsync();
            await Host.Services.GetRequiredService<AgentPromptStore>().InitializeAsync();

            MainWindow = new MainWindow();
            MainWindow.Activate();
        }
    }
 }


