using CommunityToolkit.Mvvm.ComponentModel;
using Core.Contracts;
using Core.Models;
using Core.Orchestration;
using Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AI_IT_Company.Services
{
    public sealed record PipelineEventVm(string Role, string Icon, string Payload, DateTime At);

    /// <summary>
    /// Синглтон на всё приложение. Управляет текущим запуском конвейера,
    /// хранит его состояние и события. Все страницы (Chat/Dashboard/Logs) читают отсюда.
    /// </summary>
    public sealed partial class PipelineRunService : ObservableObject
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<PipelineRunService> _logger;
        private readonly SessionStore _sessions;
        private readonly PendingChangeService _pending;
        private DispatcherQueue? _ui;

        private CancellationTokenSource? _cts;
        private Task? _currentRun;
        private AgentContext? _currentContext;
        private readonly ITranslationService _translator;

        public ObservableCollection<PipelineEventVm> Events { get; } = new();

        [ObservableProperty] private bool isRunning;
        [ObservableProperty] private string? currentProjectId;
        [ObservableProperty] private string? currentOutputRoot;
        [ObservableProperty] private string currentAgent = "";
        [ObservableProperty] private string currentStage = "";
        [ObservableProperty] private int totalStages;
        [ObservableProperty] private int completedStages;
        [ObservableProperty] private int failedStages;
        [ObservableProperty] private int skippedStages;
        [ObservableProperty] private DateTime? startedAt;
        [ObservableProperty] private string lastMessage = "";
        [ObservableProperty] private string currentMode = "";
        [ObservableProperty] private string statusText = "Готов к работе";
        [ObservableProperty] private string? lastFailedStageId;
        [ObservableProperty] private bool canRetryLastFailedStage;
        [ObservableProperty] private bool awaitingChangeReview;

        public bool HasActiveProject => !string.IsNullOrEmpty(CurrentProjectId);
        public int ProgressPercent => TotalStages == 0 ? 0
            : (int)((CompletedStages + FailedStages + SkippedStages) * 100.0 / TotalStages);

        partial void OnCurrentProjectIdChanged(string? value) => OnPropertyChanged(nameof(HasActiveProject));
        partial void OnCompletedStagesChanged(int value) => OnPropertyChanged(nameof(ProgressPercent));
        partial void OnFailedStagesChanged(int value) => OnPropertyChanged(nameof(ProgressPercent));
        partial void OnSkippedStagesChanged(int value) => OnPropertyChanged(nameof(ProgressPercent));
        partial void OnTotalStagesChanged(int value) => OnPropertyChanged(nameof(ProgressPercent));
        partial void OnIsRunningChanged(bool value) => RefreshCanRetry();
        partial void OnLastFailedStageIdChanged(string? value) => RefreshCanRetry();

        public PipelineRunService(
            IServiceProvider sp,
            ILogger<PipelineRunService> logger,
            ITranslationService translator,
            SessionStore sessions,
            PendingChangeService pending)
        {
            _sp = sp;
            _logger = logger;
            _translator = translator;
            _sessions = sessions;
            _pending = pending;
            _pending.Changed += (_, _) =>
                RunOnUi(() => AwaitingChangeReview = _pending.AwaitingUserDecision);
        }

        public void AttachUi(DispatcherQueue ui) => _ui = ui;

        public async Task StartAsync(AgentContext ctx)
        {
            if (IsRunning) return;

            _cts = new CancellationTokenSource();
            _currentContext = ctx;
            _pending.Clear();

            var pipeline = _sp.GetRequiredService<StagedPipeline>();

            RunOnUi(() =>
            {
                Events.Clear();
                IsRunning = true;
                CurrentProjectId = ctx.ProjectId;
                CurrentOutputRoot = ctx.OutputRoot ?? ctx.ProjectPath;
                CurrentAgent = "";
                CurrentStage = "";
                CompletedStages = 0;
                FailedStages = 0;
                SkippedStages = 0;
                TotalStages = 0;
                StartedAt = DateTime.Now;
                LastMessage = "";
                LastFailedStageId = null;
                CurrentMode = ModeLabel(ctx.Mode);
                StatusText = $"{CurrentMode}: запуск…";
                AwaitingChangeReview = false;
            });

            try { await _sessions.StartAsync(ctx); }
            catch (Exception ex) { _logger.LogWarning(ex, "Session start failed"); }

            await RunPipelineJobAsync(pipeline, p => p.RunAsync(ctx, _cts!.Token));
        }

        /// <summary>
        /// Повторяет последний проваленный этап и зависимые Skipped (без Architect/Scaffolder).
        /// </summary>
        public async Task RetryLastFailedStageAsync()
        {
            if (IsRunning || _currentContext?.Plan is null || string.IsNullOrEmpty(LastFailedStageId))
                return;

            var plan = _currentContext.Plan;
            var failed = plan.Stages.FirstOrDefault(s => s.Id == LastFailedStageId);
            if (failed is null) return;

            failed.Status = StageStatus.Pending;
            failed.FailReason = null;
            failed.Attempts = 0;

            foreach (var s in plan.Stages.Where(x => x.Status == StageStatus.Skipped))
            {
                s.Status = StageStatus.Pending;
                s.FailReason = null;
            }

            _cts = new CancellationTokenSource();
            var pipeline = _sp.GetRequiredService<StagedPipeline>();
            var ctx = _currentContext;

            RunOnUi(() =>
            {
                IsRunning = true;
                FailedStages = Math.Max(0, FailedStages - 1);
                SkippedStages = 0;
                LastMessage = $"Retry этапа {failed.Id}";
                StatusText = $"{CurrentMode}: повтор {failed.Id}…";
                LastFailedStageId = null;
            });

            Append(new PipelineEventVm("System", "🔁",
                $"Повтор этапа {failed.Id}: {failed.Name}", DateTime.Now));

            await RunPipelineJobAsync(pipeline, p => p.ResumeStagesAsync(ctx, _cts!.Token));
        }

        public void Stop()
        {
            if (!IsRunning || _cts is null) return;
            try { _cts.Cancel(); } catch { }
            Append(new PipelineEventVm("System", "⏹",
                "Запрошена остановка… ждём завершения текущего шага.", DateTime.Now));
            RunOnUi(() => StatusText = $"{CurrentMode}: остановка…");
        }

        public void ClearEvents() => RunOnUi(() => Events.Clear());

        private async Task RunPipelineJobAsync(
            StagedPipeline pipeline,
            Func<StagedPipeline, Task> work)
        {
            var reader = Task.Run(async () =>
            {
                try
                {
                    await foreach (var ev in pipeline.Events.Reader.ReadAllAsync(_cts!.Token))
                    {
                        var vm = new PipelineEventVm(
                            ev.Role.ToString(), KindIcon(ev.Kind),
                            Truncate(ev.Payload, 4000), ev.At.ToLocalTime());
                        Append(vm);
                        UpdateFromEvent(ev);
                    }
                }
                catch (OperationCanceledException) { }
            });

            _currentRun = Task.Run(async () =>
            {
                try
                {
                    await work(pipeline);
                }
                catch (OperationCanceledException)
                {
                    Append(new PipelineEventVm("System", "⏹",
                        "Работа остановлена пользователем.", DateTime.Now));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Pipeline error.");
                    Append(new PipelineEventVm("System", "❌",
                        "Ошибка: " + ex.Message, DateTime.Now));
                }
                finally
                {
                    try { await reader; } catch { }
                    var status = FailedStages > 0 ? "Failed" : "Succeeded";
                    if (_cts?.IsCancellationRequested == true) status = "Cancelled";
                    var summary = $"{CurrentMode}: {status}; stages ok={CompletedStages} fail={FailedStages} skip={SkippedStages}";
                    var sid = CurrentProjectId;
                    if (!string.IsNullOrEmpty(sid))
                    {
                        try { await _sessions.FinishAsync(sid, status, summary); }
                        catch (Exception ex) { _logger.LogWarning(ex, "Session finish failed"); }
                    }

                    RunOnUi(() =>
                    {
                        IsRunning = false;
                        CurrentAgent = "";
                        StatusText = FailedStages > 0
                            ? $"{CurrentMode}: завершено с ошибками"
                            : $"{CurrentMode}: готово";
                        AwaitingChangeReview = _pending.AwaitingUserDecision;
                        RefreshCanRetry();
                    });
                }
            });

            await Task.CompletedTask;
        }

        private void UpdateFromEvent(PipelineEvent ev)
        {
            RunOnUi(() =>
            {
                LastMessage = $"{ev.Role}: {Truncate(ev.Payload, 200)}";

                switch (ev.Kind)
                {
                    case "start":
                        CurrentAgent = ev.Role.ToString();
                        StatusText = ComposeStatus();
                        break;
                    case "scan":
                        StatusText = $"{CurrentMode}: сканирование — {Truncate(ev.Payload, 80)}";
                        break;
                    case "info":
                        StatusText = $"{CurrentMode}: {Truncate(ev.Payload, 100)}";
                        break;
                    case "stage-start":
                        CurrentStage = ev.Payload;
                        StatusText = ComposeStatus();
                        break;
                    case "stage-ok":
                        CompletedStages++;
                        break;
                    case "stage-fail":
                        FailedStages++;
                        // Payload: "S1: reason" — берём id до двоеточия/точки
                        LastFailedStageId = ExtractStageId(ev.Payload) ?? ExtractStageId(CurrentStage);
                        break;
                    case "acceptance-ok":
                        StatusText = $"{CurrentMode}: приёмка OK";
                        break;
                    case "acceptance-fail":
                        StatusText = $"{CurrentMode}: приёмка провалена";
                        break;
                    case "pending-change":
                        StatusText = $"{CurrentMode}: ожидание Apply/Reject — {Truncate(ev.Payload, 80)}";
                        AwaitingChangeReview = true;
                        break;
                    case "terminal":
                        // статус не затираем длинным логом
                        break;
                }

                if (TotalStages == 0 && _currentContext?.Plan is not null)
                    TotalStages = _currentContext.Plan.Stages.Count;

                if (_currentContext?.Plan is { } plan)
                {
                    int skipped = plan.Stages.Count(s => s.Status == StageStatus.Skipped);
                    if (skipped != SkippedStages) SkippedStages = skipped;
                }
            });

            if (ev.Kind is "done"
                && ev.Role is not AgentRole.Architect and not AgentRole.Secretary
                    and not AgentRole.Documenter and not AgentRole.Artist and not AgentRole.Analyst
                    and not AgentRole.UxReviewer
                && ev.Payload.Length > 200
                && _translator.IsEnabled)
            {
                _ = Task.Run(async () =>
                {
                    var t = await _translator.ToUserAsync(ev.Payload);
                    RunOnUi(() =>
                    {
                        for (int i = Events.Count - 1; i >= 0; i--)
                            if (Events[i].Role == ev.Role.ToString() && Events[i].Payload == Truncate(ev.Payload, 4000))
                            {
                                Events[i] = Events[i] with { Payload = Truncate(t, 4000) };
                                break;
                            }
                    });
                });
            }
        }

        private string ComposeStatus()
        {
            var parts = new System.Collections.Generic.List<string> { CurrentMode };
            if (!string.IsNullOrWhiteSpace(CurrentAgent)) parts.Add(CurrentAgent);
            if (!string.IsNullOrWhiteSpace(CurrentStage)) parts.Add(CurrentStage);
            return string.Join(" · ", parts);
        }

        private void RefreshCanRetry()
        {
            CanRetryLastFailedStage = !IsRunning
                && !string.IsNullOrEmpty(LastFailedStageId)
                && _currentContext?.Plan is not null;
        }

        private void Append(PipelineEventVm vm)
        {
            RunOnUi(() =>
            {
                Events.Add(vm);
                while (Events.Count > 500) Events.RemoveAt(0);
            });
        }

        private void RunOnUi(Action a)
        {
            var dq = _ui;
            if (dq is null || dq.HasThreadAccess) a();
            else dq.TryEnqueue(() => a());
        }

        private static string? ExtractStageId(string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return null;
            // "S1. Name" или "S1: reason"
            var token = payload.Split(new[] { '.', ':', ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }

        public static string ModeLabel(WorkMode mode) => mode switch
        {
            WorkMode.CreateNew => "Создание",
            WorkMode.Improve => "Улучшение",
            WorkMode.FixError => "Исправление",
            WorkMode.Document => "Документирование",
            WorkMode.Analyze => "Анализ",
            WorkMode.PlanArchitecture => "ТЗ / архитектура",
            _ => mode.ToString()
        };

        private static string KindIcon(string kind) => kind switch
        {
            "start" => "▶",
            "done" => "✅",
            "error" => "❌",
            "warn" => "⚠",
            "info" => "ℹ",
            "scan" => "🔎",
            "git" => "🌿",
            "git-reset" => "⏪",
            "stage-start" => "🎯",
            "stage-ok" => "✅",
            "stage-fail" => "❌",
            "acceptance-ok" => "☑",
            "acceptance-fail" => "☒",
            "stages-summary" => "📊",
            "terminal" => "⌨",
            "pending-change" => "📝",
            "stage-docs" => "📚",
            _ => "•"
        };

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
    }
}
