using Build;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Configuration;
using Core.Models;
using Core.Services;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace AI_IT_Company.ViewModels;

public partial class ChangesViewModel : ObservableObject
{
    private readonly PendingChangeService _pending;
    private readonly AppSettingsStore _settings;
    private readonly CorrectionLessonStore _lessons;
    private DispatcherQueue? _ui;

    public ObservableCollection<PendingChangeItemVm> Items { get; } = new();

    [ObservableProperty] private PendingChangeItemVm? selected;
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private bool awaitingDecision;
    [ObservableProperty] private string diffText = "";
    [ObservableProperty] private bool showTeachAfterReject;
    [ObservableProperty] private string rejectTeachWrong = "";
    [ObservableProperty] private string rejectTeachRight = "";
    [ObservableProperty] private string rejectTeachStatus = "";

    public ChangesViewModel(
        PendingChangeService pending,
        AppSettingsStore settings,
        CorrectionLessonStore lessons)
    {
        _pending = pending;
        _settings = settings;
        _lessons = lessons;
        _pending.Changed += (_, _) => EnqueueRefresh();
    }

    public void AttachUi()
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        Refresh();
    }

    partial void OnSelectedChanged(PendingChangeItemVm? value)
        => DiffText = value?.Diff ?? "";

    private void EnqueueRefresh()
    {
        var dq = _ui ?? DispatcherQueue.GetForCurrentThread();
        if (dq is null) return;
        if (dq.HasThreadAccess) Refresh();
        else dq.TryEnqueue(Refresh);
    }

    public void Refresh()
    {
        var snap = _pending.Snapshot();
        Items.Clear();
        foreach (var c in snap)
            Items.Add(new PendingChangeItemVm(c));
        AwaitingDecision = _pending.AwaitingUserDecision;
        StatusText = AwaitingDecision
            ? $"Waiting Apply/Reject · pending {_pending.PendingCount}"
            : $"Changes: {snap.Count} · pending {_pending.PendingCount} · mode {_settings.GetReviewChangesMode()}";
        if (Selected is not null)
        {
            var match = Items.FirstOrDefault(i => i.RelativePath == Selected.RelativePath);
            Selected = match;
            DiffText = match?.Diff ?? "";
        }
    }

    [RelayCommand]
    private async Task ApplySelectedAsync()
    {
        foreach (var item in Items)
            item.Model.IsSelected = item.IsSelected;
        await _pending.ApplySelectedAsync();
        Refresh();
    }

    [RelayCommand]
    private async Task ApplyAllAsync()
    {
        await _pending.ApplyAllAsync();
        Refresh();
    }

    [RelayCommand]
    private void RejectSelected()
    {
        foreach (var item in Items)
            item.Model.IsSelected = item.IsSelected;

        var rejected = Items.Where(i => i.IsSelected && i.Model.Status == PendingChangeStatus.Pending).ToList();
        _pending.RejectSelected();
        if (rejected.Count > 0)
        {
            RejectTeachWrong = "Rejected AI change: " + string.Join(", ",
                rejected.Take(5).Select(r => r.RelativePath));
            RejectTeachRight = "";
            ShowTeachAfterReject = true;
            RejectTeachStatus = "Optional: explain the correct approach and save a lesson.";
        }
        Refresh();
    }

    [RelayCommand]
    private void RejectAll()
    {
        var rejected = Items.Where(i => i.Model.Status == PendingChangeStatus.Pending).ToList();
        _pending.RejectAll();
        if (rejected.Count > 0)
        {
            RejectTeachWrong = "Rejected AI change: " + string.Join(", ",
                rejected.Take(5).Select(r => r.RelativePath));
            RejectTeachRight = "";
            ShowTeachAfterReject = true;
            RejectTeachStatus = "Optional: explain the correct approach and save a lesson.";
        }
        Refresh();
    }

    [RelayCommand]
    private void SkipRejectTeach()
    {
        ShowTeachAfterReject = false;
        RejectTeachWrong = "";
        RejectTeachRight = "";
        RejectTeachStatus = "";
    }

    [RelayCommand]
    private async Task SaveRejectTeachAsync()
    {
        if (string.IsNullOrWhiteSpace(RejectTeachRight))
        {
            RejectTeachStatus = "Fill in how it should be done.";
            return;
        }

        var ctx = Selected?.Diff ?? "";
        try
        {
            await _lessons.AddAsync(
                "Any",
                "CodeReject",
                string.IsNullOrWhiteSpace(RejectTeachWrong) ? "(rejected change)" : RejectTeachWrong,
                RejectTeachRight,
                ctx);
            RejectTeachStatus = "Lesson saved.";
            ShowTeachAfterReject = false;
            RejectTeachWrong = "";
            RejectTeachRight = "";
        }
        catch (Exception ex)
        {
            RejectTeachStatus = "Failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenInEditor()
    {
        if (Selected is null) return;
        ExternalEditorLauncher.Open(
            Selected.FullPath,
            _settings.GetExternalEditorPath(),
            _settings.GetExternalEditorArgs());
    }

    [RelayCommand]
    private void ClearHistory()
    {
        if (_pending.AwaitingUserDecision) return;
        _pending.Clear();
        Refresh();
    }
}

public partial class PendingChangeItemVm : ObservableObject
{
    public PendingChange Model { get; }
    public string RelativePath => Model.RelativePath;
    public string FullPath => Model.FullPath;
    public string Status => Model.Status.ToString();
    public string Diff => Model.UnifiedDiff;
    public string Source => Model.SourceKey;

    [ObservableProperty] private bool isSelected;

    public PendingChangeItemVm(PendingChange model)
    {
        Model = model;
        IsSelected = model.IsSelected;
    }
}
