using CommunityToolkit.Mvvm.ComponentModel;
using Core.Contracts;
using Core.Orchestration;
using Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
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
        private DispatcherQueue? _ui;

        private CancellationTokenSource? _cts;
        private Task? _currentRun;
        private AgentContext? _currentContext;
        ITranslationService _translator;

        // ===== Реактивное состояние =====
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

        public bool HasActiveProject => !string.IsNullOrEmpty(CurrentProjectId);
        public int ProgressPercent => TotalStages == 0 ? 0
            : (int)((CompletedStages + FailedStages + SkippedStages) * 100.0 / TotalStages);

        partial void OnCurrentProjectIdChanged(string? value) => OnPropertyChanged(nameof(HasActiveProject));
        partial void OnCompletedStagesChanged(int value) => OnPropertyChanged(nameof(ProgressPercent));
        partial void OnFailedStagesChanged(int value) => OnPropertyChanged(nameof(ProgressPercent));
        partial void OnSkippedStagesChanged(int value) => OnPropertyChanged(nameof(ProgressPercent));
        partial void OnTotalStagesChanged(int value) => OnPropertyChanged(nameof(ProgressPercent));

        public PipelineRunService(IServiceProvider sp, ILogger<PipelineRunService> logger, ITranslationService translator)
        {
            _sp = sp;
            _logger = logger;
            _translator = translator;
        }

        /// <summary>Вызывается из App.xaml.cs после активации главного окна.</summary>
        public void AttachUi(DispatcherQueue ui) => _ui = ui;

        // ===== Публичное API =====

        public async Task StartAsync(AgentContext ctx)
        {
            if (IsRunning) return;

            _cts = new CancellationTokenSource();
            _currentContext = ctx;

            // Каждый запуск получает свой пайплайн со своим Channel событий.
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
            });

            // Читатель событий канала — фоновой задачей.
            var reader = Task.Run(async () =>
            {
                try
                {
                    await foreach (var ev in pipeline.Events.Reader.ReadAllAsync(_cts.Token))
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

            // Сам конвейер — тоже фоновой задачей.
            _currentRun = Task.Run(async () =>
            {
                try
                {
                    await pipeline.RunAsync(ctx, _cts.Token);
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
                    RunOnUi(() =>
                    {
                        IsRunning = false;
                        CurrentAgent = "";
                    });
                }
            });

            await Task.CompletedTask; // возвращаем управление UI сразу
        }

        public void Stop()
        {
            if (!IsRunning || _cts is null) return;
            try { _cts.Cancel(); } catch { }
            Append(new PipelineEventVm("System", "⏹",
                "Запрошена остановка… ждём завершения текущего шага.", DateTime.Now));
        }

        public void ClearEvents()
        {
            RunOnUi(() => Events.Clear());
        }

        // ===== Внутреннее =====

        private void UpdateFromEvent(PipelineEvent ev)
        {
            RunOnUi(() =>
            {
                LastMessage = $"{ev.Role}: {Truncate(ev.Payload, 200)}";

                switch (ev.Kind)
                {
                    case "start": CurrentAgent = ev.Role.ToString(); break;
                    case "stage-start": CurrentStage = ev.Payload; break;
                    case "stage-ok": CompletedStages++; break;
                    case "stage-fail": FailedStages++; break;
                }

                // Как только Архитектор построил план — узнаём число этапов.
                if (TotalStages == 0 && _currentContext?.Plan is not null)
                    TotalStages = _currentContext.Plan.Stages.Count;

                // Пересчёт пропущенных из плана (они помечаются пайплайном).
                if (_currentContext?.Plan is { } plan)
                {
                    int skipped = 0;
                    foreach (var s in plan.Stages)
                        if (s.Status == Core.Models.StageStatus.Skipped) skipped++;
                    if (skipped != SkippedStages) SkippedStages = skipped;
                }
            });

            // Architect/Secretary уже локализуют Output в агенте — повторный ToUserAsync не нужен.
            // "stage-summary" мёртвое: пайплайн шлёт "stages-summary" (уже на языке UI).
            if (ev.Kind is "done"
                && ev.Role is not AgentRole.Architect and not AgentRole.Secretary
                && ev.Payload.Length > 200
                && _translator.IsEnabled)
            {
                _ = Task.Run(async () =>
                {
                    var t = await _translator.ToUserAsync(ev.Payload);
                    RunOnUi(() =>
                    {
                        // ищем последнее добавленное событие и обновляем Payload
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

        private void Append(PipelineEventVm vm)
        {
            RunOnUi(() =>
            {
                Events.Add(vm);
                while (Events.Count > 500) Events.RemoveAt(0); // защита от переполнения
            });
        }

        private void RunOnUi(Action a)
        {
            var dq = _ui;
            if (dq is null || dq.HasThreadAccess) a();
            else dq.TryEnqueue(() => a());
        }

        private static string KindIcon(string kind) => kind switch
        {
            "start" => "▶",
            "done" => "✅",
            "error" => "❌",
            "warn" => "⚠",
            "git" => "🌿",
            "git-reset" => "⏪",
            "stage-start" => "🎯",
            "stage-ok" => "✅",
            "stage-fail" => "❌",
            "stages-summary" => "📊",
            _ => "•"
        };

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
    }
}
