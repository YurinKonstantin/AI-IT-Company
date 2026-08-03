using Core.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Services;

/// <summary>
/// Пауза пайплайна при исчерпании попыток авто-фикса сборки — выбор пользователя.
/// </summary>
public sealed class BuildFixGateService
{
    private readonly object _gate = new();
    private TaskCompletionSource<BuildFixUserChoice>? _waitTcs;

    public bool AwaitingDecision { get; private set; }
    public string Prompt { get; private set; } = "";
    public string? StageId { get; private set; }
    /// <summary>Truncated build log for UI while waiting.</summary>
    public string BuildLogSnippet { get; private set; } = "";

    public event EventHandler? Changed;

    public void Clear()
    {
        lock (_gate)
        {
            AwaitingDecision = false;
            Prompt = "";
            StageId = null;
            BuildLogSnippet = "";
            _waitTcs?.TrySetResult(BuildFixUserChoice.Rollback);
            _waitTcs = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<BuildFixUserChoice> WaitForDecisionAsync(
        string stageId, string prompt, CancellationToken ct)
        => WaitForDecisionAsync(stageId, prompt, buildLogSnippet: null, ct);

    public async Task<BuildFixUserChoice> WaitForDecisionAsync(
        string stageId, string prompt, string? buildLogSnippet, CancellationToken ct)
    {
        TaskCompletionSource<BuildFixUserChoice> tcs;
        lock (_gate)
        {
            AwaitingDecision = true;
            Prompt = prompt ?? "";
            StageId = stageId;
            BuildLogSnippet = TruncateTail(buildLogSnippet ?? "", 2500);
            _waitTcs = new TaskCompletionSource<BuildFixUserChoice>(
                TaskCreationOptions.RunContinuationsAsynchronously);
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
            lock (_gate) AwaitingDecision = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Resolve(BuildFixUserChoice choice)
    {
        lock (_gate)
        {
            _waitTcs?.TrySetResult(choice);
            _waitTcs = null;
            AwaitingDecision = false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string TruncateTail(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : "…\n" + s[^max..];
}
