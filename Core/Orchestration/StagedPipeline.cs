using Build;
using Core.Contracts;
using Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Core.Orchestration
{
   // public sealed record PipelineEvent(AgentRole Role, string Kind, string Payload, DateTime At);

    public sealed class StagedPipeline
    {
        private readonly Dictionary<AgentRole, IAgent> _agents;
        private readonly ILogger<StagedPipeline> _logger;

        public Channel<PipelineEvent> Events { get; } =
            Channel.CreateUnbounded<PipelineEvent>(new UnboundedChannelOptions { SingleReader = false });

        public StagedPipeline(IEnumerable<IAgent> agents, ILogger<StagedPipeline> logger)
        {
            _agents = agents.ToDictionary(a => a.Role);
            _logger = logger;
        }

        public async Task RunAsync(AgentContext ctx, CancellationToken ct)
        {
            // 1) Интерпретация задачи
            await RunAgent(AgentRole.Interpreter, ctx, ct);

            // 2) Архитектор формирует ТЗ + план + сохраняет TZ.md
            await RunAgent(AgentRole.Architect, ctx, ct);
            if (ctx.Plan is null)
            {
                await Emit(AgentRole.Architect, "error", "План не сформирован — прекращаем.");
                Events.Writer.TryComplete();
                return;
            }

            // 3) Инициализация git и baseline-коммит
            var repo = ctx.OutputRoot
                    ?? ctx.ProjectPath
                    ?? PathHelper.GetProjectOutputRoot(ctx.ProjectId);
            ctx.OutputRoot = repo;
            PathHelper.EnsureDirectory(repo);

            var gitAvailable = await GitService.IsAvailableAsync(ct);
            if (!gitAvailable)
            {
                await Emit(AgentRole.Builder, "warn", "git не установлен — работаем без версионирования.");
            }
            else
            {
                await GitService.InitIfNeededAsync(repo, ct);
                var baseline = await GitService.CommitAllAsync(repo, "chore: baseline (TZ)", ct);
                ctx.SharedData["baseline_sha"] = baseline ?? "";
                await Emit(AgentRole.Builder, "git", $"baseline: {baseline}");
            }
            // 3.5) Скаффолдинг каркаса (только для новых проектов)
            await RunAgent(AgentRole.Scaffolder, ctx, ct);

            // Обновим baseline-коммит, чтобы включить каркас
            if (gitAvailable)
            {
                var afterScaffold = await GitService.CommitAllAsync(repo, "chore: scaffold via dotnet new", ct);
                if (!string.IsNullOrEmpty(afterScaffold))
                {
                    ctx.SharedData["baseline_sha"] = afterScaffold;
                    await Emit(AgentRole.Builder, "git", $"scaffold committed: {afterScaffold}");
                }
            }
            // 4) Выполнение этапов
            await ExecuteStagesAsync(ctx, repo, gitAvailable, ct);

            // 5) Итоговый отчёт
            await RunAgent(AgentRole.Secretary, ctx, ct);
            Events.Writer.TryComplete();
        }

        // -------------------- ЭТАПЫ --------------------

        private async Task ExecuteStagesAsync(AgentContext ctx, string repo, bool gitAvailable, CancellationToken ct)
        {
            var plan = ctx.Plan!;
            string? lastSuccessSha = ctx.SharedData.GetValueOrDefault("baseline_sha", "");

            while (true)
            {
                var next = PickNextRunnable(plan);
                if (next is null) break;

                next.Status = StageStatus.InProgress;
                next.Attempts++;
                ctx.CurrentStage = next;
                await Emit(AgentRole.Architect, "stage-start", $"{next.Id}. {next.Name}");

                // Очищаем накопления предыдущего этапа
                foreach (var k in new[] { "backend_code", "frontend_code", "game_code", "tests_code", "fix_code" })
                    ctx.SharedData.Remove(k);

                // Запускаем нужных кодеров параллельно по scope
                var tasks = new List<Task>();
                if (next.Scope.Contains("Backend")) tasks.Add(RunAgent(AgentRole.BackendCoder, ctx, ct));
                if (next.Scope.Contains("Frontend")) tasks.Add(RunAgent(AgentRole.FrontendCoder, ctx, ct));
                if (next.Scope.Contains("Game")) tasks.Add(RunAgent(AgentRole.GameCoder, ctx, ct));
                await Task.WhenAll(tasks);

                if (next.Scope.Contains("Tests"))
                    await RunAgent(AgentRole.Tester, ctx, ct);

                // Сборка + до 3 попыток авто-исправления
                var buildOk = await BuildWithFixesAsync(ctx, maxAttempts: 3, ct);

                if (buildOk)
                {
                    string? sha = null;
                    if (gitAvailable)
                        sha = await GitService.CommitAllAsync(repo, $"stage({next.Id}): {next.Name}", ct);

                    next.GitCommitSha = sha;
                    next.Status = StageStatus.Succeeded;
                    if (!string.IsNullOrWhiteSpace(sha)) lastSuccessSha = sha;

                    await Emit(AgentRole.Builder, "stage-ok", $"{next.Id} → {sha ?? "(no-git)"}");
                }
                else
                {
                    next.Status = StageStatus.Failed;
                    next.FailReason = LastLine(ctx.SharedData.GetValueOrDefault("build_log", ""));
                    await Emit(AgentRole.ErrorFixer, "stage-fail", $"{next.Id}: {next.FailReason}");

                    // Откат к последнему успешному состоянию
                    if (gitAvailable && !string.IsNullOrWhiteSpace(lastSuccessSha))
                    {
                        await GitService.ResetHardAsync(repo, lastSuccessSha, ct);
                        await Emit(AgentRole.Builder, "git-reset", $"откат к {lastSuccessSha}");
                    }

                    // Все, кто зависел от провалившегося, — Skipped
                    MarkDependentsSkipped(plan, next.Id);
                }
            }

            // Итоговая сводка по этапам
            await Emit(AgentRole.Secretary, "stages-summary", RenderStagesSummary(plan));
        }

        // -------------------- ПОДБОР ЭТАПА --------------------

        /// <summary>
        /// Ищет ЛЮБОЙ Pending-этап, все зависимости которого Succeeded.
        /// Если провалился этап X, все зависимые от него уже помечены Skipped
        /// → PickNextRunnable автоматически выберет НЕЗАВИСИМЫЙ этап.
        /// </summary>
        private static Stage? PickNextRunnable(ProjectPlan plan)
        {
            return plan.Stages.FirstOrDefault(s =>
                s.Status == StageStatus.Pending &&
                s.DependsOn.All(dep =>
                    plan.Stages.First(x => x.Id == dep).Status == StageStatus.Succeeded));
        }

        private static void MarkDependentsSkipped(ProjectPlan plan, string failedId)
        {
            bool changed;
            do
            {
                changed = false;
                foreach (var s in plan.Stages.Where(x => x.Status == StageStatus.Pending))
                {
                    bool blocked = s.DependsOn.Any(dep =>
                    {
                        var st = plan.Stages.First(x => x.Id == dep).Status;
                        return st is StageStatus.Failed or StageStatus.Skipped;
                    });
                    if (blocked)
                    {
                        s.Status = StageStatus.Skipped;
                        s.FailReason = $"Пропущен: зависит от провалившегося {failedId}.";
                        changed = true;
                    }
                }
            } while (changed);
        }

        // -------------------- СБОРКА С ФИКСАМИ --------------------

        private async Task<bool> BuildWithFixesAsync(AgentContext ctx, int maxAttempts, CancellationToken ct)
        {
            await RunAgent(AgentRole.Builder, ctx, ct);
            int attempt = 0;
            while (ctx.SharedData.GetValueOrDefault("build_ok") == "false" && attempt++ < maxAttempts)
            {
                ctx.SharedData.Remove("fix_code");
                await RunAgent(AgentRole.ErrorFixer, ctx, ct);
                await RunAgent(AgentRole.Builder, ctx, ct);
            }
            return ctx.SharedData.GetValueOrDefault("build_ok") == "true";
        }

        // -------------------- УТИЛИТЫ --------------------

        private async Task RunAgent(AgentRole role, AgentContext ctx, CancellationToken ct)
        {
            if (!_agents.TryGetValue(role, out var agent))
            {
                _logger.LogWarning("Агент {Role} не зарегистрирован — пропуск.", role);
                return;
            }
            await Emit(role, "start", "");
            var result = await agent.ExecuteAsync(ctx, ct);
            await Emit(role,
                       result.Success ? "done" : "error",
                       result.Success ? Trim(result.Output) : result.Error ?? "");
        }

        private Task Emit(AgentRole role, string kind, string payload)
            => Events.Writer.WriteAsync(new PipelineEvent(role, kind, payload, DateTime.UtcNow)).AsTask();

        private static string Trim(string s, int max = 400)
            => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

        private static string LastLine(string s)
            => string.IsNullOrWhiteSpace(s)
                ? ""
                : s.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";

        private static string RenderStagesSummary(ProjectPlan plan)
        {
            var sb = new System.Text.StringBuilder("Итоги этапов:\n");
            foreach (var s in plan.Stages)
            {
                var icon = s.Status switch
                {
                    StageStatus.Succeeded => "✅",
                    StageStatus.Failed => "❌",
                    StageStatus.Skipped => "⏭",
                    _ => "•"
                };
                sb.AppendLine($"{icon} {s.Id} {s.Name} " +
                              $"(попыток: {s.Attempts}" +
                              $"{(s.GitCommitSha is null ? "" : $", sha: {s.GitCommitSha[..7]}")})");
                if (s.FailReason is not null) sb.AppendLine($"   ↳ {s.FailReason}");
            }
            return sb.ToString();
        }
    }
}