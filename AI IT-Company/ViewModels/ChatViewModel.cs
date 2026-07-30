using AI_IT_Company;
using Build;
using Build;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core;
using Core.Contracts;
using Core.Orchestration;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using System.Threading.Tasks;

namespace ViewModels;

public partial class ChatViewModel : ObservableObject
{
  //  private readonly AgentPipeline _pipeline;
    private readonly DispatcherQueue _ui;
    private readonly StagedPipeline _pipeline;

    public ObservableCollection<PipelineEvent> Events { get; } = new();

    [ObservableProperty] private int modeIndex;
    [ObservableProperty] private string prompt = "";
    [ObservableProperty] private string projectPath = "";

    // Инфо о текущем/последнем запуске.
    [ObservableProperty] private string currentProjectId = "";
    [ObservableProperty] private string currentOutputRoot = "";
    [ObservableProperty] private bool hasActiveProject;
    [ObservableProperty] private bool isRunning;

    public ChatViewModel(StagedPipeline pipeline)
    {
        _pipeline = pipeline;
        _ui = DispatcherQueue.GetForCurrentThread()
              ?? throw new InvalidOperationException("ChatViewModel должен создаваться на UI-потоке.");
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsRunning = true;
        RunCommand.NotifyCanExecuteChanged();
        Events.Clear();

        var projectId = Guid.NewGuid().ToString("N")[..8];
        var mode = ModeIndex switch
        {
            1 => WorkMode.Improve,
            2 => WorkMode.FixError,
            3 => WorkMode.Document,
            _ => WorkMode.CreateNew
        };

        // Гарантированно создаём и получаем путь.
        var outputRoot = string.IsNullOrWhiteSpace(ProjectPath)
            ? PathHelper.GetProjectOutputRoot(projectId)
            : PathHelper.EnsureDirectory(ProjectPath);

        CurrentProjectId = projectId;
        CurrentOutputRoot = outputRoot;
        HasActiveProject = true;

        var ctx = new AgentContext
        {
            ProjectId = projectId,
            UserPrompt = Prompt,
            ProjectPath = string.IsNullOrWhiteSpace(ProjectPath) ? null : ProjectPath,
            OutputRoot = outputRoot,
            Mode = mode
        };

        if (ctx.ProjectPath is not null)
        {
            var (type, files) = ProjectScanner.Scan(ctx.ProjectPath);
            if (Enum.TryParse<ProjectType>(type, out var pt)) ctx.Type = pt;
            ctx.Files.AddRange(files);
        }

        var reader = _pipeline.Events.Reader;
        _ = Task.Run(() => _pipeline.RunAsync(ctx, CancellationToken.None));

        try
        {
            await foreach (var ev in reader.ReadAllAsync())
            {
                _ui.TryEnqueue(() => Events.Add(ev));

                // Автоматически откроем папку после сохранения отчёта.
                if (ev.Role == AgentRole.Secretary && ev.Kind == "done")
                    _ui.TryEnqueue(OpenFolder);
            }


        }
        finally
        {
            IsRunning = false;
            _ui.TryEnqueue(() => RunCommand.NotifyCanExecuteChanged());
        }
    }

    private bool CanRun() => !IsRunning && !string.IsNullOrWhiteSpace(Prompt);

    partial void OnPromptChanged(string value) => RunCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(HasActiveProject))]
    private void OpenFolder()
    {
        if (string.IsNullOrWhiteSpace(CurrentOutputRoot)) return;
        var path = PathHelper.EnsureDirectory(CurrentOutputRoot);

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    partial void OnHasActiveProjectChanged(bool value)
        => OpenFolderCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void CopyProjectId()
    {
        if (string.IsNullOrWhiteSpace(CurrentProjectId)) return;
        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
        pkg.SetText(CurrentProjectId);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");

        var mainWindow = ((App)Application.Current).MainWindow;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ProjectPath = folder.Path;
    }
}