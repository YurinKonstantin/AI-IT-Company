using AI_IT_Company;
using AI_IT_Company.Services;
using Build;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core;
using Core.Configuration;
using Core.Contracts;
using Core.Models;
using Core.Services;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    /// <summary>0=Auto, 1=CreateNew, 2=Improve, 3=FixError, 4=Document, 5=Analyze, 6=PlanArchitecture</summary>
    [ObservableProperty] private int modeIndex;
    [ObservableProperty] private string modeHint = "";
    [ObservableProperty] private bool needsProjectFolder;
    [ObservableProperty] private bool isSidePanelOpen = true;
    [ObservableProperty] private bool timelineFollowTail = true;
    [ObservableProperty] private string teachWrong = "";
    [ObservableProperty] private string teachRight = "";
    [ObservableProperty] private int teachRoleIndex; // 0=Any, 1=ErrorFixer, 2=Backend, 3=Frontend, 4=Fullstack, 5=Architect
    [ObservableProperty] private string teachStatus = "";
    [ObservableProperty] private int enabledLessonCount;

    private readonly ITranslationService _translator;
    private readonly CorrectionLessonStore _lessons;
    private readonly AppSettingsStore _settings;
    private string _lastFailContext = "";
    private int _eventsSynced;

    public ObservableCollection<ContextChipVm> Attachments { get; } = new();
    public ObservableCollection<TimelineItemVm> Timeline { get; } = new();

    public string[] ModeOptions { get; } =
    {
        "Auto",
        "Create",
        "Improve",
        "Fix",
        "Document",
        "Analyze",
        "Plan only"
    };

    public string[] TeachRoleOptions { get; } =
    {
        "Any",
        "ErrorFixer",
        "BackendCoder",
        "FrontendCoder",
        "FullstackCoder",
        "Architect"
    };

    public ChatViewModel(
        PipelineRunService runner,
        ITranslationService translator,
        CorrectionLessonStore lessons,
        AppSettingsStore settings)
    {
        Runner = runner;
        _translator = translator;
        _lessons = lessons;
        _settings = settings;
        RefreshModeHint();
        Runner.Events.CollectionChanged += OnEventsChanged;
        Runner.PropertyChanged += OnRunnerPropertyChanged;
        _ = RefreshLessonCountAsync();
    }

    public bool HasBuildFixLog =>
        !string.IsNullOrWhiteSpace(Runner.BuildFixLogSnippet);

    private void OnRunnerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PipelineRunService.AwaitingBuildFixDecision)
            or nameof(PipelineRunService.AwaitingChangeReview)
            or nameof(PipelineRunService.BuildFixLogSnippet))
        {
            OnPropertyChanged(nameof(HasBuildFixLog));
            if (Runner.AwaitingBuildFixDecision || Runner.AwaitingChangeReview)
                IsSidePanelOpen = true;
        }
    }

    partial void OnModeIndexChanged(int value) => RefreshModeHint();

    private void RefreshModeHint()
    {
        NeedsProjectFolder = ModeIndex is 2 or 3 or 4 or 5;
        ModeHint = ModeIndex switch
        {
            0 => AI_IT_Company.Loc.Get("ModeHint_Auto"),
            1 => AI_IT_Company.Loc.Get("ModeHint_Create"),
            2 => AI_IT_Company.Loc.Get("ModeHint_Improve"),
            3 => AI_IT_Company.Loc.Get("ModeHint_Fix"),
            4 => AI_IT_Company.Loc.Get("ModeHint_Document"),
            5 => AI_IT_Company.Loc.Get("ModeHint_Analyze"),
            6 => AI_IT_Company.Loc.Get("ModeHint_Plan"),
            _ => ""
        };
    }

    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // Keep user turns; drop agent/system from previous run sync marker
            _eventsSynced = 0;
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (PipelineEventVm ev in e.NewItems)
                AppendAgentEvent(ev);
            _eventsSynced = Runner.Events.Count;
        }
    }

    private void AppendAgentEvent(PipelineEventVm ev)
    {
        if (ev.Role is "ErrorFixer" or "Builder" ||
            (ev.Payload?.Contains("stage-fail", StringComparison.OrdinalIgnoreCase) == true) ||
            ev.Icon is "❌" or "⚠")
        {
            if (!string.IsNullOrWhiteSpace(ev.Payload))
                _lastFailContext = ev.Payload;
        }

        var kind = ev.Role is "System" or "User" ? TimelineKind.System : TimelineKind.Agent;
        // ▶ start rows carry "Source · model" — keep as body; title stays role name.
        var title = string.IsNullOrWhiteSpace(ev.Role) ? "Agent" : ev.Role;
        if (ev.Icon == "▶" && !string.IsNullOrWhiteSpace(ev.Payload))
            title = $"{ev.Role} · {ev.Payload}";

        Timeline.Add(new TimelineItemVm
        {
            Kind = kind,
            Title = title,
            Body = ev.Icon == "▶" ? "" : (ev.Payload ?? ""),
            Icon = ev.Icon,
            At = ev.At
        });
    }

    [RelayCommand]
    private void ToggleSidePanel() => IsSidePanelOpen = !IsSidePanelOpen;

    [RelayCommand]
    private void OpenReview() => MainWindow.CurrentInstance?.NavigateToTag("changes");

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
            Label = (kind == AttachmentKind.Folder ? "@folder " : "@file ")
                    + Path.GetFileName(path.TrimEnd('\\', '/'))
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

        var userText = Prompt.Trim();
        Timeline.Add(new TimelineItemVm
        {
            Kind = TimelineKind.User,
            Title = AI_IT_Company.Loc.Get("Timeline_You"),
            Body = userText,
            Icon = "›",
            At = DateTime.Now
        });

        var translated = await _translator.ToWorkingAsync(userText);

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

        ctx.SharedData["user_prompt_original"] = userText;
        ctx.SharedData["ui_mode_auto"] = isAuto.ToString().ToLowerInvariant();
        if (Attachments.Count > 0)
            ctx.SharedData["attachments"] = string.Join("; ", Attachments.Select(a => a.Label));

        Prompt = "";
        await Runner.StartAsync(ctx);
    }

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
    private async Task SaveTeachLessonAsync()
    {
        if (string.IsNullOrWhiteSpace(TeachRight))
        {
            TeachStatus = "Fill in how it should be done (RIGHT).";
            return;
        }

        var role = TeachRoleIndex >= 0 && TeachRoleIndex < TeachRoleOptions.Length
            ? TeachRoleOptions[TeachRoleIndex]
            : "Any";
        var kind = Runner.AwaitingBuildFixDecision ? "BuildError" : "ManualTeach";
        try
        {
            await _lessons.AddAsync(
                role,
                kind,
                string.IsNullOrWhiteSpace(TeachWrong) ? "(unspecified mistake)" : TeachWrong,
                TeachRight,
                _lastFailContext);
            TeachWrong = "";
            TeachRight = "";
            TeachStatus = "Lesson saved — will apply on next agent runs.";
            await RefreshLessonCountAsync();
        }
        catch (Exception ex)
        {
            TeachStatus = "Failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task RefreshLessonCountAsync()
    {
        try { EnabledLessonCount = await _lessons.CountEnabledAsync(); }
        catch { EnabledLessonCount = 0; }
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
    private void BuildFixRollback() =>
        Runner.ResolveBuildFix(BuildFixUserChoice.Rollback);

    [RelayCommand]
    private void BuildFixManualContinue() =>
        Runner.ResolveBuildFix(BuildFixUserChoice.ManualContinue);

    [RelayCommand]
    private void BuildFixProceedWithError() =>
        Runner.ResolveBuildFix(BuildFixUserChoice.ProceedWithError);

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

    [RelayCommand]
    private void OpenProjectInEditor()
    {
        var root = Runner.CurrentOutputRoot;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;

        // Prefer a .csproj / .sln / last known source for the external editor.
        string? target = null;
        try
        {
            target = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault()
                  ?? Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                        .FirstOrDefault(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                          && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        }
        catch { /* ignore */ }

        target ??= root;
        var path = _settings.GetExternalEditorPath();
        var args = _settings.GetExternalEditorArgs();
        ExternalEditorLauncher.Open(target, path, args);
    }

    [RelayCommand]
    private void ClearTimeline()
    {
        Timeline.Clear();
        Runner.ClearEvents();
        _eventsSynced = 0;
    }
}

public enum TimelineKind { User, Agent, System }

public sealed class TimelineItemVm
{
    public TimelineKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string Icon { get; init; } = "";
    public DateTime At { get; init; }
    public bool IsUser => Kind == TimelineKind.User;
    public bool IsAgent => Kind == TimelineKind.Agent;
    public bool IsSystem => Kind == TimelineKind.System;
}

public sealed class ContextChipVm
{
    public string Path { get; init; } = "";
    public AttachmentKind Kind { get; init; }
    public string Label { get; init; } = "";
}
