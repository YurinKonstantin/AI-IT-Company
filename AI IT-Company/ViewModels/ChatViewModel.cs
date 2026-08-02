using AI_IT_Company;
using AI_IT_Company.Services;
using Build;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core;
using Core.Contracts;
using Core.Models;
using Core.Services;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace ViewModels;

public partial class ChatViewModel : ObservableObject
{
    public PipelineRunService Runner { get; }

    [ObservableProperty] private string prompt = "";
    [ObservableProperty] private string projectPath = "";
    /// <summary>
    /// 0=Авто, 1=CreateNew, 2=Improve, 3=FixError, 4=Document, 5=Analyze, 6=PlanArchitecture
    /// </summary>
    [ObservableProperty] private int modeIndex;
    [ObservableProperty] private string modeHint =
        "Авто: Interpreter выберет режим. Папка нужна, если задача про существующий код.";
    [ObservableProperty] private bool needsProjectFolder;
    private readonly ITranslationService _translator;

    public ObservableCollection<ContextChipVm> Attachments { get; } = new();

    public ChatViewModel(PipelineRunService runner, ITranslationService translator)
    {
        Runner = runner;
        _translator = translator;
        RefreshModeHint();
    }

    partial void OnModeIndexChanged(int value) => RefreshModeHint();

    private void RefreshModeHint()
    {
        NeedsProjectFolder = ModeIndex is 2 or 3 or 4 or 5;
        ModeHint = ModeIndex switch
        {
            0 => "Авто: Interpreter выберет режим. Папка нужна, если задача про существующий код.",
            1 => "Создание нового проекта. Папка не обязательна — результат в Output.",
            2 => "Улучшение: нужна папка существующего проекта. Смотрите диффы на вкладке Изменения.",
            3 => "Исправление ошибки: нужна папка проекта. Читайте build_log при Failed.",
            4 => "Документирование: нужна папка. Появятся README / ARCHITECTURE, код не переписывается.",
            5 => "Анализ: нужна папка. Результат — ANALYSIS.md без правок кода.",
            6 => "Только ТЗ / архитектура: код не генерируется, выход — TZ.md и план.",
            _ => ""
        };
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowRef);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ProjectPath = folder.Path;
    }

    [RelayCommand]
    private async Task AttachFileAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowRef);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        AddAttachment(file.Path, AttachmentKind.File);
    }

    [RelayCommand]
    private async Task AttachFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowRef);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        AddAttachment(folder.Path, AttachmentKind.Folder);
    }

    [RelayCommand]
    private void RemoveAttachment(ContextChipVm? chip)
    {
        if (chip is null) return;
        Attachments.Remove(chip);
    }

    [RelayCommand]
    private void ClearAttachments() => Attachments.Clear();

    private void AddAttachment(string path, AttachmentKind kind)
    {
        if (Attachments.Any(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            return;
        Attachments.Add(new ContextChipVm
        {
            Path = path,
            Kind = kind,
            Label = (kind == AttachmentKind.Folder ? "@folder " : "@file ") + Path.GetFileName(path.TrimEnd('\\', '/'))
        });
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (Runner.IsRunning) return;
        if (string.IsNullOrWhiteSpace(Prompt)) return;

        ParseAtMentionsFromPrompt();

        bool isAuto = ModeIndex == 0;
        bool needsExisting = ModeIndex is 2 or 3 or 4 or 5;
        if (needsExisting && string.IsNullOrWhiteSpace(ProjectPath))
            return;

        var translated = await _translator.ToWorkingAsync(Prompt);

        var projectId = Guid.NewGuid().ToString("N")[..8];
        var outputRoot = string.IsNullOrWhiteSpace(ProjectPath)
            ? PathHelper.GetProjectOutputRoot(projectId)
            : PathHelper.EnsureDirectory(ProjectPath);

        var (mode, modeLocked) = ModeIndex switch
        {
            0 => (WorkMode.CreateNew, false),
            2 => (WorkMode.Improve, true),
            3 => (WorkMode.FixError, true),
            4 => (WorkMode.Document, true),
            5 => (WorkMode.Analyze, true),
            6 => (WorkMode.PlanArchitecture, true),
            _ => (WorkMode.CreateNew, true)
        };

        var ctx = new AgentContext
        {
            ProjectId = projectId,
            UserPrompt = translated,
            ProjectPath = outputRoot,
            Mode = mode,
            ModeLocked = modeLocked
        };

        foreach (var chip in Attachments)
        {
            ctx.Attachments.Add(new ContextAttachment
            {
                Path = chip.Path,
                Kind = chip.Kind,
                DisplayName = chip.Label
            });
        }

        if (ctx.ProjectPath is not null)
        {
            var scan = ProjectScanner.ScanDetailed(ctx.ProjectPath);
            if (Enum.TryParse<ProjectType>(scan.Type, out var pt)) ctx.Type = pt;
            ctx.Files.AddRange(scan.Files);
            ctx.SharedData["project_structure"] = scan.StructureTree;
            if (needsExisting || (!isAuto && ModeIndex is 2 or 3 or 4 or 5)
                || (!string.IsNullOrWhiteSpace(ProjectPath) && ctx.Files.Count > 0))
                ctx.SharedData["project_metrics"] = ProjectMetrics.CollectReport(ctx.ProjectPath, scan.Files);
        }

        ctx.SharedData["user_prompt_original"] = Prompt;
        ctx.SharedData["ui_mode_auto"] = isAuto.ToString().ToLowerInvariant();
        if (Attachments.Count > 0)
            ctx.SharedData["attachments"] = string.Join("; ", Attachments.Select(a => a.Label));

        await Runner.StartAsync(ctx);
    }

    /// <summary>Подхватывает @file path и @folder path из текста промпта.</summary>
    private void ParseAtMentionsFromPrompt()
    {
        if (string.IsNullOrWhiteSpace(Prompt)) return;
        foreach (Match m in Regex.Matches(Prompt, @"@(file|folder)\s+[""']?([^\s""']+)[""']?", RegexOptions.IgnoreCase))
        {
            var kind = m.Groups[1].Value.Equals("folder", StringComparison.OrdinalIgnoreCase)
                ? AttachmentKind.Folder
                : AttachmentKind.File;
            var path = m.Groups[2].Value.Trim();
            if (kind == AttachmentKind.File && File.Exists(path))
                AddAttachment(path, AttachmentKind.File);
            else if (kind == AttachmentKind.Folder && Directory.Exists(path))
                AddAttachment(path, AttachmentKind.Folder);
            else if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(ProjectPath))
            {
                var full = Path.GetFullPath(Path.Combine(ProjectPath, path));
                if (kind == AttachmentKind.File && File.Exists(full))
                    AddAttachment(full, AttachmentKind.File);
                else if (kind == AttachmentKind.Folder && Directory.Exists(full))
                    AddAttachment(full, AttachmentKind.Folder);
            }
        }
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

public sealed class ContextChipVm
{
    public string Path { get; init; } = "";
    public AttachmentKind Kind { get; init; }
    public string Label { get; init; } = "";
}
