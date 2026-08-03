using Build;
using Core.Configuration;
using Core.Contracts;
using Core.Services;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents;

public sealed class BuilderAgent : AgentBase
{
    public override AgentRole Role => AgentRole.Builder;
    protected override string DefaultSystemPrompt => "Sink-agent, LLM is not actively used.";
    private readonly AppSettingsStore _settings;
    private readonly PendingChangeService _pending;

    private const int MaxNuGetRetries = 3;

    public BuilderAgent(
        IAiProviderFactory factory,
        AgentConfigStore configStore,
        AgentPromptStore promptStore,
        AppSettingsStore settings,
        PendingChangeService pending,
        ILogger<BuilderAgent> logger)
        : base(factory, configStore, promptStore, logger)
    {
        _settings = settings;
        _pending = pending;
    }

    public override async Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
    {
        var outputRoot = ctx.OutputRoot
                      ?? ctx.ProjectPath
                      ?? PathHelper.GetProjectOutputRoot(ctx.ProjectId);
        PathHelper.EnsureDirectory(outputRoot);
        ctx.OutputRoot = outputRoot;

        Logger.LogInformation("[Builder] Рабочая папка: {Path}", outputRoot);

        var prevSink = DotnetRunner.TerminalSink;
        DotnetRunner.TerminalSink = line =>
            ctx.RaiseEvent?.Invoke(AgentRole.Builder, "terminal", line);

        try
        {
            return await ExecuteCoreAsync(ctx, outputRoot, ct);
        }
        finally
        {
            DotnetRunner.TerminalSink = prevSink;
        }
    }

    private async Task<AgentResult> ExecuteCoreAsync(AgentContext ctx, string outputRoot, CancellationToken ct)
    {
        // Один бэкап на прогон (важно для цикла FixError ↔ Builder).
        if (ctx.Mode != WorkMode.CreateNew
            && ctx.ProjectPath is not null
            && !ctx.SharedData.ContainsKey("backup_path"))
        {
            try
            {
                var backupFolder = PathHelper.CreateBackupFolder();
                var actualBackup = BackupService.Backup(ctx.ProjectPath, backupFolder);
                ctx.SharedData["backup_path"] = actualBackup;
                Logger.LogInformation("[Builder] Бэкап: {Path}", actualBackup);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[Builder] Бэкап не создан.");
            }
        }

        int staged = StageCoderFiles(ctx, outputRoot);
        Logger.LogInformation("[Builder] Предложено файлов: {Count}", staged);
        if (staged > 0)
        {
            ctx.RaiseEvent?.Invoke(AgentRole.Builder, "pending-change",
                $"{staged} файл(ов) в очереди изменений. Режим ревью: {_settings.GetReviewChangesMode()}");

            var applied = await _pending.WaitIfNeededAsync(ctx.Mode, ct);
            if (!applied)
            {
                ctx.SharedData["build_ok"] = "false";
                ctx.SharedData["build_log"] = "Пользователь отклонил предложенные изменения.";
                return new AgentResult(false, "", "Изменения отклонены (Reject).");
            }
        }

        var projects = NuGetPackageResolver.FindProjectFiles(outputRoot);
        if (projects.Count == 0)
        {
            var msg = "Не найден .csproj для сборки.";
            Logger.LogWarning("[Builder] {Msg}", msg);
            ctx.SharedData["build_ok"] = "false";
            ctx.SharedData["build_log"] = msg;
            return new AgentResult(false, "", msg);
        }

        var log = new StringBuilder();
        var addedPackages = new List<string>();
        var triedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) Явный restore
        var (restoreCode, restoreOut, restoreErr) = await DotnetRunner.RestoreAsync(outputRoot, ct);
        log.AppendLine("=== dotnet restore ===");
        log.AppendLine(restoreOut);
        log.AppendLine(restoreErr);

        // 2) Build + авто-установка недостающих пакетов
        int exitCode = -1;
        string stdOut = "", stdErr = "";

        for (int attempt = 0; attempt <= MaxNuGetRetries; attempt++)
        {
            (exitCode, stdOut, stdErr) = await DotnetRunner.BuildAsync(outputRoot, ct);
            log.AppendLine($"=== dotnet build (attempt {attempt + 1}) ===");
            log.AppendLine(stdOut);
            log.AppendLine(stdErr);

            if (exitCode == 0) break;

            var combined = stdOut + "\n" + stdErr + "\n" + restoreOut + "\n" + restoreErr;
            var missing = NuGetPackageResolver.ParseMissingPackages(combined)
                .Where(p => triedPackages.Add(p.PackageId))
                .ToList();

            if (missing.Count == 0 || attempt == MaxNuGetRetries)
                break;

            Logger.LogInformation(
                "[Builder] Обнаружены недостающие пакеты ({Count}): {Packages}",
                missing.Count,
                string.Join(", ", missing.Select(m => m.PackageId)));

            bool anyAdded = false;
            foreach (var pkg in missing)
            {
                var targetCsproj = PickTargetCsproj(projects, pkg.PackageId);
                if (targetCsproj is null) continue;

                // Уже есть в csproj — всё равно пробуем restore, но не add повторно.
                string csprojText;
                try { csprojText = await File.ReadAllTextAsync(targetCsproj, ct); }
                catch { csprojText = ""; }

                if (NuGetPackageResolver.CsprojHasPackage(csprojText, pkg.PackageId))
                {
                    Logger.LogInformation(
                        "[Builder] Пакет {Pkg} уже в {Csproj} — повторный restore",
                        pkg.PackageId, Path.GetFileName(targetCsproj));
                    var (rc, ro, re) = await DotnetRunner.RestoreAsync(outputRoot, ct);
                    log.AppendLine($"=== restore (existing {pkg.PackageId}) ===");
                    log.AppendLine(ro);
                    log.AppendLine(re);
                    anyAdded = rc == 0 || anyAdded;
                    continue;
                }

                Logger.LogInformation(
                    "[Builder] dotnet add package {Pkg} → {Csproj} ({Reason})",
                    pkg.PackageId, Path.GetFileName(targetCsproj), pkg.Reason);

                var (ok, addLog) = await DotnetRunner.AddPackageAsync(
                    targetCsproj, pkg.PackageId, pkg.Version, ct);
                log.AppendLine($"=== dotnet add package {pkg.PackageId} ===");
                log.AppendLine(addLog);

                if (ok)
                {
                    anyAdded = true;
                    addedPackages.Add(pkg.PackageId);
                }
                else
                {
                    Logger.LogWarning("[Builder] Не удалось добавить {Pkg}", pkg.PackageId);
                }
            }

            if (!anyAdded) break;

            // После add — restore перед следующим build.
            var (rCode, rOut, rErr) = await DotnetRunner.RestoreAsync(outputRoot, ct);
            log.AppendLine("=== dotnet restore (after add) ===");
            log.AppendLine(rOut);
            log.AppendLine(rErr);
            restoreOut = rOut;
            restoreErr = rErr;
        }

        if (addedPackages.Count > 0)
        {
            ctx.SharedData["nuget_packages_added"] = string.Join(", ", addedPackages.Distinct());
            Logger.LogInformation("[Builder] Установлены NuGet-пакеты: {Pkgs}",
                ctx.SharedData["nuget_packages_added"]);
        }

        ctx.SharedData["build_ok"] = (exitCode == 0).ToString().ToLowerInvariant();
        ctx.SharedData["build_log"] = log.ToString();

        var summary = exitCode == 0
            ? stdOut
            : stdErr;
        if (addedPackages.Count > 0)
            summary = $"NuGet added: {string.Join(", ", addedPackages)}\n" + summary;

        return new AgentResult(
            Success: exitCode == 0,
            Output: summary,
            Error: exitCode == 0 ? null : TrimEnd(stdErr, 2000));
    }

    private int StageCoderFiles(AgentContext ctx, string outputRoot)
    {
        int staged = 0;
        var coderMode = _settings.GetCoderMode();
        string[] keys = coderMode == CoderMode.Unified
            ? new[] { "fullstack_code", "game_code", "tests_code", "fix_code" }
            : new[] { "backend_code", "frontend_code", "game_code", "tests_code", "fix_code" };

        var errorFiles = ctx.SharedData.GetValueOrDefault("build_error_files", "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            if (!ctx.SharedData.TryGetValue(key, out var blob)) continue;

            int filesThisKey = 0;
            foreach (var b in CodeExtractor.Extract(blob))
            {
                if (b.Path is null) continue;
                if (!FileWriteGuard.TryResolve(outputRoot, b.Path, out var fullPath, out var deny))
                {
                    Logger.LogWarning("[Builder] Запись отклонена ({Path}): {Reason}", b.Path, deny);
                    continue;
                }

                // ErrorFixer: hard cap files per fix blob.
                if (key == "fix_code" && filesThisKey >= ErrorFixerAgent.MaxFilesPerAttempt)
                {
                    Logger.LogWarning("[Builder] fix_code: skip {Path} — max {Max} files",
                        b.Path, ErrorFixerAgent.MaxFilesPerAttempt);
                    continue;
                }

                try
                {
                    bool isCsprojDiff = b.IsDiff
                        && b.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

                    var textToStage = b.Code;
                    var isSearchReplace = IsSearchReplaceBlock(b);

                    if (isSearchReplace && !isCsprojDiff)
                    {
                        string original = File.Exists(fullPath) ? File.ReadAllText(fullPath) : "";
                        var (ok, newText, err) = SearchReplaceApplier.Apply(original, b.Code);
                        if (!ok)
                        {
                            Logger.LogWarning(
                                "[Builder] SEARCH/REPLACE failed for {Path}: {Err} — leaving for full-file fallback if present",
                                b.Path, err);
                            continue;
                        }
                        textToStage = newText;
                    }
                    else if (key == "fix_code" && !isCsprojDiff
                             && !ShouldAcceptFullRewrite(fullPath, b.Code, b.Path, errorFiles))
                    {
                        Logger.LogWarning(
                            "[Builder] Rejected oversized full rewrite of {Path} (prefer SEARCH/REPLACE)",
                            b.Path);
                        continue;
                    }

                    _pending.Stage(outputRoot, b.Path, textToStage, key, isCsprojDiff);
                    staged++;
                    filesThisKey++;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "[Builder] Не удалось поставить в очередь {Path}", b.Path);
                }
            }

            // После постановки в очередь очищаем blob, чтобы не дублировать на следующем Builder.
            ctx.SharedData.Remove(key);
        }
        return staged;
    }

    private static bool IsSearchReplaceBlock(CodeExtractor.Block b)
        => b.Lang.Contains("search-replace", StringComparison.OrdinalIgnoreCase)
           || b.Lang.Equals("patch", StringComparison.OrdinalIgnoreCase)
           || SearchReplaceApplier.LooksLikeSearchReplace(b.Code);

    /// <summary>
    /// Guardrail: reject full-file rewrites that replace most of a large existing file
    /// when the path is not in the structured error list.
    /// </summary>
    private static bool ShouldAcceptFullRewrite(
        string fullPath, string newText, string relativePath, HashSet<string> errorFiles)
    {
        if (!File.Exists(fullPath)) return true;

        string oldText;
        try { oldText = File.ReadAllText(fullPath); }
        catch { return true; }

        if (oldText.Length < 800) return true;

        var rel = relativePath.Replace('\\', '/');
        var inErrors = errorFiles.Count == 0
            || errorFiles.Contains(rel)
            || errorFiles.Any(e =>
                Path.GetFileName(e).Equals(Path.GetFileName(rel), StringComparison.OrdinalIgnoreCase));

        // If file is on the error list, allow rewrite but still reject near-total replacement of huge files
        // unless the new content shares a reasonable prefix with the old (model kept the start).
        var prefixLen = 0;
        var maxCheck = Math.Min(200, Math.Min(oldText.Length, newText.Length));
        while (prefixLen < maxCheck && oldText[prefixLen] == newText[prefixLen]) prefixLen++;

        var sizeRatio = newText.Length / (double)Math.Max(1, oldText.Length);
        var almostTotalRewrite = prefixLen < 40 && oldText.Length > 1500
            && (sizeRatio < 0.4 || sizeRatio > 2.5 || Math.Abs(newText.Length - oldText.Length) > oldText.Length * 0.6);

        if (almostTotalRewrite && !inErrors)
            return false;

        // Even on error list: refuse if model dumped a tiny stub over a large file.
        if (almostTotalRewrite && newText.Length < oldText.Length * 0.35 && oldText.Length > 2500)
            return false;

        return true;
    }

    /// <summary>
    /// Выбирает .csproj для добавления пакета: game → MonoGame project, tests → *Tests*, иначе первый.
    /// </summary>
    private static string? PickTargetCsproj(IReadOnlyList<string> projects, string packageId)
    {
        if (projects.Count == 0) return null;
        if (projects.Count == 1) return projects[0];

        bool isGamePkg = packageId.Contains("MonoGame", StringComparison.OrdinalIgnoreCase);
        bool isTestPkg = packageId.Contains("xunit", StringComparison.OrdinalIgnoreCase)
                      || packageId.Contains("Moq", StringComparison.OrdinalIgnoreCase)
                      || packageId.Contains("FluentAssertions", StringComparison.OrdinalIgnoreCase)
                      || packageId.Contains("NUnit", StringComparison.OrdinalIgnoreCase)
                      || packageId.Contains("NSubstitute", StringComparison.OrdinalIgnoreCase);

        if (isGamePkg)
        {
            var game = projects.FirstOrDefault(p =>
            {
                try
                {
                    var t = File.ReadAllText(p);
                    return t.Contains("MonoGame", StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            });
            if (game is not null) return game;
        }

        if (isTestPkg)
        {
            var test = projects.FirstOrDefault(p =>
                Path.GetFileNameWithoutExtension(p).Contains("Test", StringComparison.OrdinalIgnoreCase));
            if (test is not null) return test;
        }

        // Предпочитаем проект ближе к корню (не test).
        return projects.FirstOrDefault(p =>
                   !Path.GetFileNameWithoutExtension(p).Contains("Test", StringComparison.OrdinalIgnoreCase))
               ?? projects[0];
    }

    private static string TrimEnd(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[^max..];

    protected override string BuildUserPrompt(AgentContext ctx) => "";
}
