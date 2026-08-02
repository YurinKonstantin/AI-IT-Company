using Build;
using Core.Configuration;
using Core.Contracts;
using Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Services;

/// <summary>
/// Очередь предложенных правок (Cursor-like Apply/Reject).
/// </summary>
public sealed class PendingChangeService
{
    private readonly AppSettingsStore _settings;
    private readonly object _gate = new();
    private readonly List<PendingChange> _changes = new();
    private TaskCompletionSource<bool>? _waitTcs; // true=applied, false=rejected

    public bool AwaitingUserDecision { get; private set; }
    public event EventHandler? Changed;

    public PendingChangeService(AppSettingsStore settings) => _settings = settings;

    public int PendingCount
    {
        get { lock (_gate) return _changes.Count(c => c.Status == PendingChangeStatus.Pending); }
    }

    public IReadOnlyList<PendingChange> Snapshot()
    {
        lock (_gate) return _changes.ToList();
    }

    public bool ShouldReview(WorkMode mode)
    {
        var modeSetting = _settings.Get(AppSettingsStore.KeyReviewChangesMode, AppSettingsStore.KeyReviewChangesModeDefault);
        return modeSetting.Trim().ToLowerInvariant() switch
        {
            "never" or "off" => false,
            "always" => true,
            _ => mode is WorkMode.Improve or WorkMode.FixError
        };
    }

    public void Clear()
    {
        lock (_gate)
        {
            _changes.Clear();
            AwaitingUserDecision = false;
            _waitTcs?.TrySetResult(true);
            _waitTcs = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public PendingChange Stage(
        string outputRoot,
        string relativePath,
        string newText,
        string sourceKey,
        bool isCsprojDiff = false)
    {
        if (!FileWriteGuard.TryResolve(outputRoot, relativePath, out var full, out _))
            throw new InvalidOperationException("path rejected: " + relativePath);

        string? oldText = null;
        if (File.Exists(full) && !isCsprojDiff)
        {
            try { oldText = File.ReadAllText(full); } catch { oldText = null; }
        }

        var change = new PendingChange
        {
            RelativePath = relativePath.Replace('\\', '/'),
            FullPath = full,
            OldText = oldText,
            NewText = newText,
            IsCsprojDiff = isCsprojDiff,
            SourceKey = sourceKey,
            UnifiedDiff = isCsprojDiff
                ? $"(csproj diff patch)\n{newText}"
                : UnifiedDiff.Generate(oldText, newText, relativePath.Replace('\\', '/'))
        };

        lock (_gate)
        {
            _changes.RemoveAll(c =>
                c.RelativePath.Equals(change.RelativePath, StringComparison.OrdinalIgnoreCase)
                && c.Status == PendingChangeStatus.Pending);
            _changes.Insert(0, change);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return change;
    }

    public async Task<bool> WaitIfNeededAsync(WorkMode mode, CancellationToken ct)
    {
        if (!ShouldReview(mode))
        {
            await ApplyInternalAsync(all: true, selectedOnly: false, ct);
            return true;
        }

        if (PendingCount == 0) return true;

        TaskCompletionSource<bool> tcs;
        lock (_gate)
        {
            AwaitingUserDecision = true;
            _waitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs = _waitTcs;
        }
        Changed?.Invoke(this, EventArgs.Empty);

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try
        {
            return await tcs.Task;
        }
        finally
        {
            lock (_gate) AwaitingUserDecision = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task ApplySelectedAsync(CancellationToken ct = default)
    {
        var ok = await ApplyInternalAsync(all: false, selectedOnly: true, ct);
        lock (_gate) { _waitTcs?.TrySetResult(ok); _waitTcs = null; AwaitingUserDecision = false; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ApplyAllAsync(CancellationToken ct = default)
    {
        var ok = await ApplyInternalAsync(all: true, selectedOnly: false, ct);
        lock (_gate) { _waitTcs?.TrySetResult(ok); _waitTcs = null; AwaitingUserDecision = false; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RejectAll()
    {
        lock (_gate)
        {
            foreach (var c in _changes.Where(x => x.Status == PendingChangeStatus.Pending))
                c.Status = PendingChangeStatus.Rejected;
            _waitTcs?.TrySetResult(false);
            _waitTcs = null;
            AwaitingUserDecision = false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> ApplyInternalAsync(bool all, bool selectedOnly, CancellationToken ct)
    {
        List<PendingChange> batch;
        lock (_gate)
        {
            batch = _changes
                .Where(c => c.Status == PendingChangeStatus.Pending)
                .Where(c => all || !selectedOnly || c.IsSelected)
                .ToList();
        }

        int applied = 0;
        foreach (var c in batch)
        {
            try
            {
                var dir = Path.GetDirectoryName(c.FullPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (c.IsCsprojDiff && File.Exists(c.FullPath))
                    await CsprojDiffApplier.ApplyAsync(c.FullPath, c.NewText, ct);
                else
                    await File.WriteAllTextAsync(c.FullPath, c.NewText, ct);

                c.Status = PendingChangeStatus.Applied;
                applied++;
            }
            catch
            {
                c.Status = PendingChangeStatus.Rejected;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return applied > 0 || batch.Count == 0;
    }
}
