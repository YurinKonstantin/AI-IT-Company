using AI_IT_Company;
using AI_IT_Company.Services;
using Build;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core;
using Core.Contracts;
using Core.Orchestration;
using Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace ViewModels;

public partial class ChatViewModel : ObservableObject
{
    public PipelineRunService Runner { get; }

    [ObservableProperty] private string prompt = "";
    [ObservableProperty] private string projectPath = "";
    [ObservableProperty] private int modeIndex;   // 0..4
    private readonly ITranslationService _translator;

    public ChatViewModel(PipelineRunService runner, ITranslationService translator)
    {
        Runner = runner;
        _translator = translator;
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        //var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(((App)App.Current).MainWindow);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowRef);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ProjectPath = folder.Path;
    }

    [RelayCommand]
    private async Task RunAsync()
    {

        if (Runner.IsRunning) return;
        if (string.IsNullOrWhiteSpace(Prompt)) return;

        // Improve / Fix / Document / Analyze требуют существующий проект.
        bool needsExisting = ModeIndex is 1 or 2 or 3 or 4;
        if (needsExisting && string.IsNullOrWhiteSpace(ProjectPath))
            return;

        // Переводим пользовательский промпт на рабочий язык агентов.
        var translated = await _translator.ToWorkingAsync(Prompt);

        var projectId = Guid.NewGuid().ToString("N")[..8];
        // Гарантированно создаём и получаем путь.
        var outputRoot = string.IsNullOrWhiteSpace(ProjectPath)
            ? PathHelper.GetProjectOutputRoot(projectId)
            : PathHelper.EnsureDirectory(ProjectPath);

        var ctx = new AgentContext
        {
            ProjectId = projectId,
            UserPrompt = translated,
            ProjectPath = outputRoot,
            Mode = ModeIndex switch
            {
                1 => WorkMode.Improve,
                2 => WorkMode.FixError,
                3 => WorkMode.Document,
                4 => WorkMode.Analyze,
                _ => WorkMode.CreateNew
            },
            // Режим выбран пользователем в UI — Interpreter не перезаписывает.
            ModeLocked = true
        };
        if (ctx.ProjectPath is not null)
        {
            var scan = ProjectScanner.ScanDetailed(ctx.ProjectPath);
            if (Enum.TryParse<ProjectType>(scan.Type, out var pt)) ctx.Type = pt;
            ctx.Files.AddRange(scan.Files);
            ctx.SharedData["project_structure"] = scan.StructureTree;
            if (needsExisting)
                ctx.SharedData["project_metrics"] = ProjectMetrics.CollectReport(ctx.ProjectPath, scan.Files);
        }
        ctx.SharedData["user_prompt_original"] = Prompt;
        await Runner.StartAsync(ctx);
    }

    [RelayCommand]
    private void Stop() => Runner.Stop();

    [RelayCommand]
    private async Task RetryFailedStageAsync()
    {
        if (!Runner.CanRetryLastFailedStage) return;
        await Runner.RetryLastFailedStageAsync();
    }

    [RelayCommand]
    private void CopyProjectId()
    {
        if (string.IsNullOrEmpty(Runner.CurrentProjectId)) return;
        var pkg = new DataPackage();
        pkg.SetText(Runner.CurrentProjectId);
        Clipboard.SetContent(pkg);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var p = Runner.CurrentOutputRoot;
        if (string.IsNullOrEmpty(p)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p}\"") { UseShellExecute = true }); }
        catch { }
    }
}