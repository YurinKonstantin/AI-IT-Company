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
    private DispatcherQueue? _ui;

    public ObservableCollection<PendingChangeItemVm> Items { get; } = new();

    [ObservableProperty] private PendingChangeItemVm? selected;
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private bool awaitingDecision;
    [ObservableProperty] private string diffText = "";

    public ChangesViewModel(PendingChangeService pending, AppSettingsStore settings)
    {
        _pending = pending;
        _settings = settings;
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
            ? $"Ожидание Apply/Reject · pending {_pending.PendingCount}"
            : $"Изменений: {snap.Count} · pending {_pending.PendingCount} · режим {_settings.GetReviewChangesMode()}";
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
    private void RejectAll()
    {
        _pending.RejectAll();
        Refresh();
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
