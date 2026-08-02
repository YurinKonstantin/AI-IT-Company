using Build;
using Core.Contracts;
using Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        private readonly Configuration.AppSettingsStore _settings;

        public Channel<PipelineEvent> Events { get; } =
            Channel.CreateUnbounded<PipelineEvent>(new UnboundedChannelOptions { SingleReader = false });

        public StagedPipeline(IEnumerable<IAgent> agents, Configuration.AppSettingsStore settings, ILogger<StagedPipeline> logger)
        {
            _agents = agents.ToDictionary(a => a.Role);
            _settings = settings;
            _logger = logger;
        }

        public async Task RunAsync(AgentContext ctx, CancellationToken ct)
        {
            AttachEventSink(ctx);

            // 1) Интерпретация задачи (режим UI с ModeLocked не перезаписывается)
            await RunAgent(AgentRole.Interpreter, ctx, ct);

            var intent = ctx.SharedData.GetValueOrDefault("interpreter_intent", "?");
            var conf = ctx.SharedData.GetValueOrDefault("interpreter_confidence", "?");
            await Emit(AgentRole.Interpreter, "info",
                $"Определено: {ctx.Type} · {ctx.Mode} · intent={intent} (confidence {conf})");

            if (ctx.SharedData.GetValueOrDefault("interpreter_low_confidence") == "true")
            {
                await Emit(AgentRole.Interpreter, "warn",
                    "Низкая уверенность классификации — fallback: CreateNew. Уточните задачу или выберите режим вручную.");
            }

            bool needsExisting = ctx.Mode is WorkMode.Improve or WorkMode.FixError
                or WorkMode.Document or WorkMode.Analyze
                || ctx.SharedData.GetValueOrDefault("interpreter_needs_existing") == "true";

            if (needsExisting && ModeLooksLikeEmptyOutput(ctx))
            {
                await Emit(AgentRole.Interpreter, "error",
                    "Для этого режима нужна папка существующего проекта. Укажите путь и повторите.");
                Events.Writer.TryComplete();
                return;
            }

            var repo = ctx.OutputRoot
                    ?? ctx.ProjectPath
                    ?? PathHelper.GetProjectOutputRoot(ctx.ProjectId);
            ctx.OutputRoot = repo;
            PathHelper.EnsureDirectory(repo);

            switch (ctx.Mode)
            {
                case WorkMode.Document:
                    await RunDocumentationAsync(ctx, repo, ct);
                    Events.Writer.TryComplete();
                    return;

                case WorkMode.Analyze:
                    await RunAnalyzeAsync(ctx, repo, ct);
                    Events.Writer.TryComplete();
                    return;

                case WorkMode.FixError:
                    await RunFixErrorAsync(ctx, repo, ct);
                    Events.Writer.TryComplete();
                    return;

                case WorkMode.Improve:
                    await RunImproveAsync(ctx, repo, ct);
                    Events.Writer.TryComplete();
                    return;

                case WorkMode.PlanArchitecture:
                    await RunPlanArchitectureAsync(ctx, repo, ct);
                    Events.Writer.TryComplete();
                    return;

                default:
                    // CreateNew — полный путь
                    await RunCreateNewAsync(ctx, repo, ct);
                    Events.Writer.TryComplete();
                    return;
            }
        }

        /// <summary>
        /// Только ТЗ / архитектура: Architect → Secretary, без scaffold/кодеров/сборки.
        /// </summary>
        private async Task RunPlanArchitectureAsync(AgentContext ctx, string repo, CancellationToken ct)
        {
            await Emit(AgentRole.Architect, "info",
                "Режим PlanArchitecture: только ТЗ и план, без кода и сборки.");

            if (Directory.Exists(repo) && Directory.EnumerateFileSystemEntries(repo).Any())
                RefreshProjectScan(ctx, repo);

            await RunAgent(AgentRole.Architect, ctx, ct);
            if (ctx.Plan is null)
            {
                await Emit(AgentRole.Architect, "error", "План/ТЗ не сформирован — прекращаем.");
                return;
            }

            try
            {
                var tzPath = Path.Combine(repo, "TZ.md");
                var body = new StringBuilder();
                body.AppendLine($"# {ctx.Plan.Title}");
                body.AppendLine();
                body.AppendLine(ctx.Plan.Summary ?? "");
                body.AppendLine();
                body.AppendLine("## Features");
                foreach (var f in ctx.Plan.Features ?? [])
                    body.AppendLine($"- **{f.Name}**: {f.Description} ({f.Advantage})");
                body.AppendLine();
                body.AppendLine("## Stages");
                foreach (var s in ctx.Plan.Stages ?? [])
                    body.AppendLine($"- `{s.Id}` {s.Name}: {s.Goal}");
                await File.WriteAllTextAsync(tzPath, body.ToString(), ct);
                ctx.SharedData["tz_path"] = tzPath;
                await Emit(AgentRole.Architect, "info", $"TZ.md → {tzPath}");
            }
            catch (Exception ex)
            {
                await Emit(AgentRole.Architect, "warn", "Не удалось записать TZ.md: " + ex.Message);
            }

            await RunAgent(AgentRole.Secretary, ctx, ct);
        }

        /// <summary>
        /// Путь ещё не указывает на реальный проект (только свежий output root без исходников).
        /// </summary>
        private static bool ModeLooksLikeEmptyOutput(AgentContext ctx)
        {
            var path = ctx.ProjectPath ?? ctx.OutputRoot;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return true;

            // Если сканер уже нашёл файлы — проект есть.
            if (ctx.Files.Count > 0) return false;

            try
            {
                return !Directory.EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories).Any()
                    && !Directory.EnumerateFiles(path, "*.sln", SearchOption.AllDirectories).Any()
                    && !Directory.EnumerateFiles(path, "*.slnx", SearchOption.AllDirectories).Any();
            }
            catch { return true; }
        }

        private async Task RunCreateNewAsync(AgentContext ctx, string repo, CancellationToken ct)
        {
            await RunAgent(AgentRole.Architect, ctx, ct);
            if (ctx.Plan is null)
            {
                await Emit(AgentRole.Architect, "error", "План не сформирован — прекращаем.");
                return;
            }

            var gitAvailable = await InitGitBaselineAsync(repo, "chore: baseline (TZ)", ctx, ct);

            await RunAgent(AgentRole.Scaffolder, ctx, ct);

            if (gitAvailable)
            {
                var afterScaffold = await GitService.CommitAllAsync(repo, "chore: scaffold via dotnet new", ct);
                if (!string.IsNullOrEmpty(afterScaffold))
                {
                    ctx.SharedData["baseline_sha"] = afterScaffold;
                    await Emit(AgentRole.Builder, "git", $"scaffold committed: {afterScaffold}");
                }
            }

            RefreshProjectScan(ctx, repo);
            await ExecuteStagesAsync(ctx, repo, gitAvailable, ct);
            await RunAgent(AgentRole.Secretary, ctx, ct);
        }

        /// <summary>
        /// Повтор только оставшихся/сброшенных этапов (без Architect/Scaffolder).
        /// </summary>
        public async Task ResumeStagesAsync(AgentContext ctx, CancellationToken ct)
        {
            AttachEventSink(ctx);

            var repo = ctx.OutputRoot
                    ?? ctx.ProjectPath
                    ?? PathHelper.GetProjectOutputRoot(ctx.ProjectId);
            ctx.OutputRoot = repo;
            PathHelper.EnsureDirectory(repo);

            if (ctx.Plan is null)
            {
                await Emit(AgentRole.Architect, "error", "Нет плана — Resume невозможен.");
                Events.Writer.TryComplete();
                return;
            }

            RefreshProjectScan(ctx, repo);
            var gitAvailable = await GitService.IsAvailableAsync(ct);
            await Emit(AgentRole.Builder, "info", "Resume: повтор этапов с Pending.");
            await ExecuteStagesAsync(ctx, repo, gitAvailable, ct);
            await RunAgent(AgentRole.Secretary, ctx, ct);
            Events.Writer.TryComplete();
        }

        /// <summary>
        /// Improve: сканирование → Architect (дельта) → этапы без Scaffolder → Secretary.
        /// </summary>
        private async Task RunImproveAsync(AgentContext ctx, string repo, CancellationToken ct)
        {
            await Emit(AgentRole.Architect, "info", "Режим Improve: без скаффолдинга, только изменения.");
            RefreshProjectScan(ctx, repo);
            ctx.SharedData["project_metrics"] = ProjectMetrics.CollectReport(repo, ctx.Files);
            await Emit(AgentRole.Architect, "scan",
                $"Файлов: {ctx.Files.Count}; тип: {ctx.Type}");

            if (ctx.Files.Count == 0)
            {
                await Emit(AgentRole.Architect, "warn",
                    "Исходники не найдены — Improve ожидает существующий проект.");
            }

            await RunAgent(AgentRole.Architect, ctx, ct);
            if (ctx.Plan is null)
            {
                await Emit(AgentRole.Architect, "error", "План улучшений не сформирован — прекращаем.");
                return;
            }

            var gitAvailable = await InitGitBaselineAsync(repo, "chore: baseline before improve", ctx, ct);
            // Scaffolder пропускается (Mode != CreateNew).
            await ExecuteStagesAsync(ctx, repo, gitAvailable, ct);
            await RunAgent(AgentRole.Secretary, ctx, ct);
        }

        /// <summary>
        /// FixError: короткий путь ErrorFixer ↔ Builder без Architect/Scaffolder/кодеров.
        /// </summary>
        private async Task RunFixErrorAsync(AgentContext ctx, string repo, CancellationToken ct)
        {
            await Emit(AgentRole.ErrorFixer, "info",
                "Режим FixError: Architect и кодеры пропущены.");

            RefreshProjectScan(ctx, repo);
            await Emit(AgentRole.ErrorFixer, "scan",
                $"Файлов: {ctx.Files.Count}; тип: {ctx.Type}");

            // Описание ошибки от пользователя — стартовый контекст для Fixer.
            ctx.SharedData["build_log"] =
                "=== USER ERROR REPORT ===\n" + ctx.UserPrompt + "\n";

            var gitAvailable = await InitGitBaselineAsync(repo, "fix: baseline before error fix", ctx, ct);

            // Сначала пробуем собрать — получим реальный лог.
            await RunAgent(AgentRole.Builder, ctx, ct);

            bool ok = ctx.SharedData.GetValueOrDefault("build_ok") == "true";
            if (!ok)
            {
                // Классический цикл исправлений по логу сборки.
                ok = await ContinueFixLoopAsync(ctx, maxAttempts: 5, ct);
            }
            else
            {
                // Сборка зелёная, но пользователь описал runtime/логическую ошибку —
                // даём Fixer один (или несколько) проходов по описанию.
                await Emit(AgentRole.ErrorFixer, "info",
                    "Сборка успешна — исправляем по описанию пользователя.");
                for (int i = 0; i < 3; i++)
                {
                    ctx.SharedData.Remove("fix_code");
                    // Сохраняем user report + последний build log.
                    var log = ctx.SharedData.GetValueOrDefault("build_log", "");
                    if (!log.Contains("USER ERROR REPORT", StringComparison.Ordinal))
                    {
                        ctx.SharedData["build_log"] =
                            "=== USER ERROR REPORT ===\n" + ctx.UserPrompt + "\n\n" + log;
                    }

                    await RunAgent(AgentRole.ErrorFixer, ctx, ct);
                    var fixedAny = !string.IsNullOrWhiteSpace(
                        ctx.SharedData.GetValueOrDefault("last_fixed_files", ""));
                    await RunAgent(AgentRole.Builder, ctx, ct);
                    ok = ctx.SharedData.GetValueOrDefault("build_ok") == "true";
                    if (!fixedAny) break;
                    if (!ok)
                    {
                        ok = await ContinueFixLoopAsync(ctx, maxAttempts: 3, ct);
                        break;
                    }
                }
            }

            if (gitAvailable)
            {
                var msg = ok ? "fix: error fix applied" : "fix: error fix attempted (build still failing)";
                var sha = await GitService.CommitAllAsync(repo, msg, ct);
                if (!string.IsNullOrEmpty(sha))
                    await Emit(AgentRole.Builder, "git", $"fix committed: {sha}");
            }

            await Emit(AgentRole.ErrorFixer, ok ? "done" : "warn",
                ok ? "Сборка успешна после исправлений." : "Сборка всё ещё падает — см. build_log.");
            await RunAgent(AgentRole.Secretary, ctx, ct);
        }

        private async Task<bool> ContinueFixLoopAsync(AgentContext ctx, int maxAttempts, CancellationToken ct)
        {
            int attempt = 0;
            while (ctx.SharedData.GetValueOrDefault("build_ok") == "false" && attempt++ < maxAttempts)
            {
                await Emit(AgentRole.ErrorFixer, "info",
                    $"Fix loop {attempt}/{maxAttempts}: ErrorFixer → Builder");
                ctx.SharedData.Remove("fix_code");
                await RunAgent(AgentRole.ErrorFixer, ctx, ct);
                var fixedFiles = ctx.SharedData.GetValueOrDefault("last_fixed_files", "");
                if (!string.IsNullOrWhiteSpace(fixedFiles))
                    await Emit(AgentRole.ErrorFixer, "info", "fixed: " + fixedFiles);
                await RunAgent(AgentRole.Builder, ctx, ct);
            }
            return ctx.SharedData.GetValueOrDefault("build_ok") == "true";
        }

        /// <summary>
        /// Analyze: метрики + Analyst → ANALYSIS.md, без изменения кода и сборки.
        /// </summary>
        private async Task RunAnalyzeAsync(AgentContext ctx, string repo, CancellationToken ct)
        {
            await Emit(AgentRole.Analyst, "info", "Режим Analyze: код не изменяется, сборка не запускается.");
            RefreshProjectScan(ctx, repo);
            ctx.SharedData["project_metrics"] = ProjectMetrics.CollectReport(repo, ctx.Files);
            await Emit(AgentRole.Analyst, "scan",
                $"Файлов: {ctx.Files.Count}; тип: {ctx.Type}");

            await RunAgent(AgentRole.Analyst, ctx, ct);
            await RunAgent(AgentRole.Secretary, ctx, ct);
        }

        private async Task<bool> InitGitBaselineAsync(
            string repo, string message, AgentContext ctx, CancellationToken ct)
        {
            var gitAvailable = await GitService.IsAvailableAsync(ct);
            if (!gitAvailable)
            {
                await Emit(AgentRole.Builder, "warn", "git не установлен — работаем без версионирования.");
                return false;
            }

            await GitService.InitIfNeededAsync(repo, ct);
            var baseline = await GitService.CommitAllAsync(repo, message, ct);
            ctx.SharedData["baseline_sha"] = baseline ?? "";
            await Emit(AgentRole.Builder, "git", $"baseline: {baseline}");
            return true;
        }

        /// <summary>
        /// Document: сканирование → Documenter → (git) → Secretary.
        /// Без Architect-этапов, Scaffolder, кодеров, Builder и ErrorFixer.
        /// </summary>
        private async Task RunDocumentationAsync(AgentContext ctx, string repo, CancellationToken ct)
        {
            await Emit(AgentRole.Documenter, "info", "Режим документирования: кодеры и сборка пропущены.");

            RefreshProjectScan(ctx, repo);
            await Emit(AgentRole.Documenter, "scan",
                $"Файлов: {ctx.Files.Count}; тип: {ctx.Type}");

            if (ctx.Files.Count == 0)
            {
                await Emit(AgentRole.Documenter, "warn",
                    "В указанной папке не найдено исходников — Documenter всё равно попробует описать пустой проект.");
            }

            var gitAvailable = await GitService.IsAvailableAsync(ct);
            if (gitAvailable)
            {
                await GitService.InitIfNeededAsync(repo, ct);
                var baseline = await GitService.CommitAllAsync(repo, "docs: baseline before documentation", ct);
                ctx.SharedData["baseline_sha"] = baseline ?? "";
                await Emit(AgentRole.Builder, "git", $"docs baseline: {baseline}");
            }

            await RunAgent(AgentRole.Documenter, ctx, ct);

            if (gitAvailable)
            {
                var sha = await GitService.CommitAllAsync(repo, "docs: generated project documentation", ct);
                if (!string.IsNullOrEmpty(sha))
                    await Emit(AgentRole.Builder, "git", $"docs committed: {sha}");
            }

            await RunAgent(AgentRole.Secretary, ctx, ct);
        }

        private static void RefreshProjectScan(AgentContext ctx, string repo)
        {
            if (!Directory.Exists(repo)) return;

            var scan = ProjectScanner.ScanDetailed(repo);
            ctx.Files.Clear();
            ctx.Files.AddRange(scan.Files);
            ctx.SharedData["project_structure"] = scan.StructureTree;
            ctx.SharedData["project_previews"] = ProjectScanner.BuildFilePreviews(repo, scan.Files);

            if (ctx.Type == ProjectType.Unknown
                && Enum.TryParse<ProjectType>(scan.Type, out var pt))
            {
                ctx.Type = pt;
            }
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

                // Актуальные файлы для релевантного контекста кодеров.
                RefreshProjectScan(ctx, repo);

                // Очищаем накопления предыдущего этапа
                foreach (var k in new[] { "backend_code", "frontend_code", "game_code", "tests_code", "fix_code", "fullstack_code" })
                    ctx.SharedData.Remove(k);

                // Кодеры выполняются ПОСЛЕДОВАТЕЛЬНО, чтобы:
                //   - не грузить слабый ПК двумя большими контекстами одновременно;
                //   - каждый следующий кодер видел код предыдущего и опирался на его контракты.
                // Порядок: Backend → Frontend → Game.
                // Game обычно живёт отдельно от Backend/Frontend, но если сценарий смешанный —
                // запускается последним и получает весь предыдущий контекст.

                // Определяем текущий режим кодера — читаем каждый раз, чтобы можно было менять на лету.
                var coderMode = _settings.GetCoderMode();

                bool wantsBackend = next.Scope.Contains("Backend");
                bool wantsFrontend = next.Scope.Contains("Frontend");

                bool wantsDocs = next.Scope.Contains("Docs", StringComparer.OrdinalIgnoreCase);
                bool wantsCode = wantsBackend || wantsFrontend
                    || next.Scope.Contains("Game", StringComparer.OrdinalIgnoreCase)
                    || next.Scope.Contains("Tests", StringComparer.OrdinalIgnoreCase);

                // Документация этапа — без кодеров и сборки.
                if (wantsDocs)
                    await RunAgent(AgentRole.Documenter, ctx, ct);

                if (coderMode == CoderMode.Unified && (wantsBackend || wantsFrontend))
                {
                    // Один агент делает всё за один заход.
                    await RunAgent(AgentRole.FullstackCoder, ctx, ct);
                }
                else
                {
                    // Классика: Backend, потом Frontend. Frontend видит код Backend.
                    if (wantsBackend) await RunAgent(AgentRole.BackendCoder, ctx, ct);
                    if (wantsFrontend) await RunAgent(AgentRole.FrontendCoder, ctx, ct);
                }

                if (next.Scope.Contains("Game", StringComparer.OrdinalIgnoreCase))
                {
                    // Сначала художник генерирует спрайты, затем MGCB/Content, затем кодер.
                    await RunAgent(AgentRole.Artist, ctx, ct);
                    await EnsureMgcbContentAsync(ctx, repo, ct);
                    await RunAgent(AgentRole.GameCoder, ctx, ct);
                }

                // UX-ревью после UI-кода (WinUI / MAUI) — только отчёт, без правок.
                if (wantsFrontend
                    && ctx.Type is ProjectType.WinUI or ProjectType.Maui)
                {
                    await RunAgent(AgentRole.UxReviewer, ctx, ct);
                }

                if (next.Scope.Contains("Tests", StringComparer.OrdinalIgnoreCase))
                    await RunAgent(AgentRole.Tester, ctx, ct);

                bool buildOk;
                if (wantsDocs && !wantsCode)
                {
                    // Только Docs — сборка не нужна.
                    ctx.SharedData["build_ok"] = "true";
                    buildOk = true;
                    await Emit(AgentRole.Documenter, "stage-docs", $"{next.Id}: документация без сборки");
                }
                else
                {
                    // Сборка + до 3 попыток авто-исправления
                    buildOk = await BuildWithFixesAsync(ctx, maxAttempts: 3, ct);
                }

                // Чеклист приёмки: test + MonoGame smoke.
                if (buildOk && !(wantsDocs && !wantsCode))
                {
                    var acceptance = await StageAcceptance.RunAsync(ctx, next, repo, ct);
                    await Emit(AgentRole.Builder,
                        acceptance.Ok ? "acceptance-ok" : "acceptance-fail",
                        acceptance.Summary);
                    if (!acceptance.Ok)
                    {
                        buildOk = false;
                        ctx.SharedData["build_ok"] = "false";
                        var prev = ctx.SharedData.GetValueOrDefault("build_log", "");
                        ctx.SharedData["build_log"] = prev + "\n" + acceptance.Log;
                    }
                }

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
                await Emit(AgentRole.ErrorFixer, "info",
                    $"Шаг фикса {attempt}/{maxAttempts}: анализ build_log → правка → повторная сборка");
                ctx.SharedData.Remove("fix_code");
                await RunAgent(AgentRole.ErrorFixer, ctx, ct);
                var analysis = ctx.SharedData.GetValueOrDefault("last_fix_analysis", "");
                var fixedFiles = ctx.SharedData.GetValueOrDefault("last_fixed_files", "");
                if (!string.IsNullOrWhiteSpace(analysis))
                    await Emit(AgentRole.ErrorFixer, "info", TruncatePayload("analysis: " + analysis, 500));
                if (!string.IsNullOrWhiteSpace(fixedFiles))
                    await Emit(AgentRole.ErrorFixer, "info", "fixed: " + fixedFiles);
                await RunAgent(AgentRole.Builder, ctx, ct);
            }
            return ctx.SharedData.GetValueOrDefault("build_ok") == "true";
        }

        private void AttachEventSink(AgentContext ctx)
        {
            ctx.RaiseEvent = (role, kind, payload) =>
            {
                Events.Writer.TryWrite(new PipelineEvent(role, kind, payload ?? "", DateTime.UtcNow));
            };
        }

        private static string TruncatePayload(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

        // -------------------- CONTENT / MGCB --------------------

        private async Task EnsureMgcbContentAsync(AgentContext ctx, string repo, CancellationToken ct)
        {
            try
            {
                var result = await MgcbContentBuilder.EnsureAsync(repo, ct);
                ctx.SharedData["mgcb_summary"] = result.Summary;
                ctx.SharedData["mgcb_sprites"] = result.SpriteCount.ToString();
                if (result.MgcbPath is not null)
                    ctx.SharedData["mgcb_path"] = "Content/Content.mgcb";
                await Emit(AgentRole.Artist, result.SpriteCount > 0 ? "info" : "warn", result.Summary);
            }
            catch (Exception ex)
            {
                await Emit(AgentRole.Artist, "warn", "MGCB/Content step failed: " + ex.Message);
            }
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