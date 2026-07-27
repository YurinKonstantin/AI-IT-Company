using AI_IT_Company;
using Build;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Contracts;
using Core.Orchestration;
using Microsoft.UI.Dispatching;   // ← DispatcherQueue
using Microsoft.UI.Xaml;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AiItCompany.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly AgentPipeline _pipeline;
    private readonly DispatcherQueue _ui;

    public ObservableCollection<PipelineEvent> Events { get; } = new();

    [ObservableProperty] private int modeIndex;
    [ObservableProperty] private string prompt = "";
    [ObservableProperty] private string projectPath = "";

    public ChatViewModel(AgentPipeline pipeline)
    {
        _pipeline = pipeline;
        // Захватываем UI-очередь в момент создания VM (это UI-поток).
        _ui = DispatcherQueue.GetForCurrentThread()
              ?? throw new InvalidOperationException(
                  "ChatViewModel должен создаваться на UI-потоке.");
    }
    [RelayCommand]
    private void OpenOutputFolder()
    {
        var last = Events.LastOrDefault(e => e.Kind == "output_root");
        if (last is null) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = last.Payload,
            UseShellExecute = true
        });
    }
    [RelayCommand]
    private async Task RunAsync()
    {
        Events.Clear();
        var ctx = new AgentContext
        {
            ProjectId = Guid.NewGuid().ToString("N")[..8],
            UserPrompt = Prompt,
            ProjectPath = string.IsNullOrWhiteSpace(ProjectPath) ? null : ProjectPath,
            Mode = ModeIndex switch
            {
                1 => WorkMode.Improve,
                2 => WorkMode.FixError,
                3 => WorkMode.Document,
                _ => WorkMode.CreateNew
            }
        };

        if (ctx.ProjectPath is not null)
        {
            var (type, files) = ProjectScanner.Scan(ctx.ProjectPath);
            if (Enum.TryParse<ProjectType>(type, out var pt)) ctx.Type = pt;
            ctx.Files.AddRange(files);
        }

        var reader = _pipeline.Events.Reader;

        // Пайплайн работает в фоне.
        _ = Task.Run(() => _pipeline.RunAsync(ctx, CancellationToken.None));

        // Читаем события и маршалим в UI-поток через захваченную очередь.
        await foreach (var ev in reader.ReadAllAsync())
        {
            _ui.TryEnqueue(() => Events.Add(ev));
        }
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(
            ((App)Application.Current).MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ProjectPath = folder.Path;
    }
}