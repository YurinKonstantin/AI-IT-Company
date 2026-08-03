using AI_IT_Company.Services;
using Ai.Freelance;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core;
using Core.Configuration;
using Core.Contracts;
using Core.Freelance;
using Core.Services;
using Data;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace AI_IT_Company.ViewModels;

public partial class FreelanceViewModel : ObservableObject
{
    private readonly FreelanceHuntService _hunt;
    private readonly FreelanceJobStore _store;
    private readonly FreelanceStatsService _stats;
    private readonly ITranslationService _translator;
    private readonly AppSettingsStore _settings;
    private readonly GitHubBountyMarketplaceAdapter _github;
    public PipelineRunService Runner { get; }

    public ObservableCollection<FreelanceJobItemVm> Feed { get; } = new();
    public ObservableCollection<FreelanceJobItemVm> Active { get; } = new();
    public ObservableCollection<FreelanceJobItemVm> ReviewQueue { get; } = new();
    public ObservableCollection<string> TopTagsLines { get; } = new();
    public ObservableCollection<string> AuditLines { get; } = new();

    [ObservableProperty] private FreelanceJobItemVm? selected;
    [ObservableProperty] private string statusText = "Готов к охоте";
    [ObservableProperty] private string huntLog = "";
    [ObservableProperty] private bool isHunting;
    [ObservableProperty] private int reviewRating = 4;
    [ObservableProperty] private string reviewNotes = "";
    [ObservableProperty] private bool reviewSuccess = true;
    [ObservableProperty] private string statsSummary = "Статистика: нет данных";
    [ObservableProperty] private string simulationHint = "";

    public FreelanceViewModel(
        FreelanceHuntService hunt,
        FreelanceJobStore store,
        FreelanceStatsService stats,
        PipelineRunService runner,
        ITranslationService translator,
        AppSettingsStore settings,
        GitHubBountyMarketplaceAdapter github)
    {
        _hunt = hunt;
        _store = store;
        _stats = stats;
        Runner = runner;
        _translator = translator;
        _settings = settings;
        _github = github;
        Runner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PipelineRunService.IsRunning) && !Runner.IsRunning)
                _ = MarkRunningJobsForReviewAsync();
        };
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            SimulationHint = _hunt.IsSimulationOnly
                ? "Режим симуляции: охота и score без Accept/автозапуска"
                : "";

            var newJobs = await _store.ListAsync(nameof(FreelanceJobStatus.New), 80);
            var skipped = await _store.ListAsync(nameof(FreelanceJobStatus.Skipped), 20);
            Feed.Clear();
            foreach (var j in newJobs.Concat(skipped).OrderByDescending(j => j.FinalScore))
                Feed.Add(FreelanceJobItemVm.From(j));

            Active.Clear();
            foreach (var s in new[] { nameof(FreelanceJobStatus.Accepted), nameof(FreelanceJobStatus.Running) })
                foreach (var j in await _store.ListAsync(s, 40))
                    Active.Add(FreelanceJobItemVm.From(j));

            ReviewQueue.Clear();
            foreach (var j in await _store.ListAsync(nameof(FreelanceJobStatus.AwaitingUserReview), 40))
                ReviewQueue.Add(FreelanceJobItemVm.From(j));

            var dash = await _stats.GetDashboardAsync();
            StatsSummary =
                $"Win-rate {(dash.WinRate * 100):0.#}% ({dash.Successes}/{dash.Outcomes}) · " +
                $"avg ★{dash.AvgRating:0.0} · expected profit {dash.ExpectedProfitSum} · " +
                $"actual revenue {dash.ActualRevenueSum} · Δ {dash.ProfitDelta}";

            TopTagsLines.Clear();
            foreach (var t in dash.TopTags)
                TopTagsLines.Add($"{t.Tag}: {t.Successes}/{t.Attempts} ({t.WinRate * 100:0}%) ★{t.AvgRating:0.0} bias {t.FeasibilityBias:+0.0;-0.0;0}");

            AuditLines.Clear();
            foreach (var a in dash.RecentAudit)
                AuditLines.Add(a);

            StatusText = $"Лента: {Feed.Count} · активные: {Active.Count} · на оценке: {ReviewQueue.Count}";
        }
        catch (Exception ex)
        {
            StatusText = "Ошибка загрузки: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task HuntAsync()
    {
        if (IsHunting) return;
        IsHunting = true;
        StatusText = "Охота…";
        try
        {
            var (offers, saved, deduped, log, autoIds) = await _hunt.HuntAsync();
            HuntLog = log;
            StatusText = $"Найдено {offers}, сохранено {saved}, дедуп {deduped}. " +
                         $"Источники: {string.Join(", ", _hunt.GetEnabledSources())}";
            await RefreshAsync();

            if (!_hunt.IsSimulationOnly)
            {
                foreach (var id in autoIds)
                {
                    var full = await _store.GetAsync(id);
                    if (full is null) continue;
                    await AcceptCoreAsync(full, auto: true);
                }
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            StatusText = "Охота не удалась: " + ex.Message;
            HuntLog = ex.ToString();
        }
        finally { IsHunting = false; }
    }

    [RelayCommand]
    private async Task AcceptAsync()
    {
        if (Selected is null) return;
        if (_hunt.IsSimulationOnly)
        {
            StatusText = "Симуляция: Accept отключён. Выключите Simulation in Settings.";
            return;
        }
        var job = await _store.GetAsync(Selected.Id);
        if (job is null) return;
        await AcceptCoreAsync(job, auto: false);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task SkipAsync()
    {
        if (Selected is null) return;
        await _store.SetStatusAsync(Selected.Id, FreelanceJobStatus.Skipped);
        await _stats.WriteAuditAsync("Skip", Selected.Id, Selected.Title);
        StatusText = $"Пропущено: {Selected.Title}";
        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenOnMarketplace()
    {
        if (Selected is null || string.IsNullOrWhiteSpace(Selected.Url)) return;
        try { Process.Start(new ProcessStartInfo(Selected.Url) { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void OpenOutput()
    {
        var path = Selected?.OutputRoot;
        if (string.IsNullOrWhiteSpace(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private async Task SubmitReviewAsync()
    {
        var job = Selected ?? ReviewQueue.FirstOrDefault();
        if (job is null) return;
        await _store.SaveOutcomeAsync(job.Id, ReviewSuccess, ReviewRating, null, null, ReviewNotes ?? "");
        await _stats.ApplyOutcomeCalibrationAsync(job.Id, ReviewSuccess, ReviewRating);
        await _stats.WriteAuditAsync("Review", job.Id,
            $"{(ReviewSuccess ? "success" : "fail")} ★{ReviewRating}");
        StatusText = ReviewSuccess
            ? $"Успех ★{ReviewRating} — калибровка обновлена"
            : $"Провал ★{ReviewRating} — калибровка обновлена";
        ReviewNotes = "";
        await RefreshAsync();
    }

    private async Task AcceptCoreAsync(FreelanceJobRecord job, bool auto)
    {
        if (Runner.IsRunning)
        {
            StatusText = "Пайплайн уже запущен — дождитесь завершения.";
            return;
        }

        // Opt-in GitHub draft comment — never without the settings flag; skip in simulation.
        if (string.Equals(job.Source, "GitHub", StringComparison.OrdinalIgnoreCase)
            && _settings.GetFreelanceGitHubDraftComment()
            && !_hunt.IsSimulationOnly)
        {
            try
            {
                var offer = new FreelanceJobOffer
                {
                    ExternalId = job.ExternalId,
                    Source = job.Source,
                    Title = job.Title,
                    Description = job.Description,
                    Url = job.Url
                };
                var body = $"""
                    Draft interest from AI IT-Company (automated, please review before treating as a bid).

                    **Task:** {job.Title}
                    We are evaluating feasibility and may open a PR if the scope fits.

                    — posted only because "GitHub draft comment on Accept" is enabled in settings.
                    """;
                var (ok, msg) = await _github.PostDraftCommentAsync(offer, body);
                await _stats.WriteAuditAsync(ok ? "GitHubDraftComment" : "GitHubDraftCommentFail",
                    job.Id, msg);
                if (!ok)
                    StatusText = "GitHub draft comment failed: " + msg + " — pipeline continues.";
            }
            catch (Exception ex)
            {
                await _stats.WriteAuditAsync("GitHubDraftCommentFail", job.Id, ex.Message);
                StatusText = "GitHub draft comment error: " + ex.Message;
            }
        }

        var projectId = "fl" + Guid.NewGuid().ToString("N")[..6];
        var outputRoot = PathHelper.EnsureDirectory(
            System.IO.Path.Combine(PathHelper.OutputRoot, "Freelance", Sanitize(job.ExternalId)));

        var prompt = $"""
            Freelance task from {job.Source}
            Title: {job.Title}
            Budget: {job.BudgetMin}-{job.BudgetMax} {job.Currency}
            Tags: {job.Tags}
            URL: {job.Url}

            {job.Description}
            """;

        var translated = await _translator.ToWorkingAsync(prompt);
        var mode = InferMode(job);

        var ctx = new AgentContext
        {
            ProjectId = projectId,
            UserPrompt = translated,
            ProjectPath = outputRoot,
            Mode = mode,
            ModeLocked = true
        };
        ctx.SharedData["user_prompt_original"] = prompt;
        ctx.SharedData["freelance_job_id"] = job.Id;
        ctx.SharedData["freelance_source"] = job.Source;

        await _store.SetStatusAsync(job.Id, FreelanceJobStatus.Running, projectId, outputRoot);
        await _stats.WriteAuditAsync(auto ? "AutoAccept" : "Accept", job.Id,
            $"pipeline={projectId} score={job.FinalScore}");
        StatusText = (auto ? "Авто-принятие → " : "Принято → ") + job.Title;
        await Runner.StartAsync(ctx);
    }

    private async Task MarkRunningJobsForReviewAsync()
    {
        try
        {
            var running = await _store.ListAsync(nameof(FreelanceJobStatus.Running), 50);
            foreach (var j in running)
            {
                if (!string.IsNullOrEmpty(j.PipelineProjectId)
                    && j.PipelineProjectId == Runner.CurrentProjectId)
                {
                    await _store.SetStatusAsync(j.Id, FreelanceJobStatus.AwaitingUserReview);
                    await _stats.WriteAuditAsync("AwaitingReview", j.Id, "pipeline finished");
                }
            }
            await RefreshAsync();
        }
        catch { /* ignore */ }
    }

    private static WorkMode InferMode(FreelanceJobRecord job)
    {
        var t = (job.Title + " " + job.Description).ToLowerInvariant();
        if (t.Contains("fix") || t.Contains("bug") || t.Contains("ошибк")) return WorkMode.FixError;
        if (t.Contains("improve") || t.Contains("refactor") || t.Contains("доработ")) return WorkMode.Improve;
        return WorkMode.CreateNew;
    }

    private static string Sanitize(string s)
    {
        var chars = s.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray();
        var name = new string(chars);
        return string.IsNullOrEmpty(name) ? Guid.NewGuid().ToString("N")[..8] : name[..Math.Min(40, name.Length)];
    }
}

public sealed class FreelanceJobItemVm
{
    public string Id { get; init; } = "";
    public string Source { get; init; } = "";
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Status { get; init; } = "";
    public int FinalScore { get; init; }
    public int Feasibility { get; init; }
    public string Profit { get; init; } = "";
    public string Recommendation { get; init; } = "";
    public string Explain { get; init; } = "";
    public string Tags { get; init; } = "";
    public string Budget { get; init; } = "";
    public string? OutputRoot { get; init; }
    public string SummaryLine => $"[{Source}] ★{FinalScore} · feas {Feasibility} · {Recommendation} · profit {Profit}";

    public static FreelanceJobItemVm From(FreelanceJobRecord j) => new()
    {
        Id = j.Id,
        Source = j.Source,
        Title = j.Title,
        Url = j.Url,
        Status = j.Status,
        FinalScore = j.FinalScore,
        Feasibility = j.FeasibilityScore,
        Profit = j.ExpectedProfit,
        Recommendation = j.Recommendation,
        Explain = j.Explain,
        Tags = j.Tags,
        Budget = $"{j.BudgetMin}-{j.BudgetMax} {j.Currency}",
        OutputRoot = j.OutputRoot
    };
}
