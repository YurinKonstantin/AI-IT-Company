using Build;
using Core.Configuration;
using Core.Contracts;
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

    private const int MaxNuGetRetries = 3;

    public BuilderAgent(
        IAiProviderFactory factory,
        AgentConfigStore configStore,
        AgentPromptStore promptStore,
        AppSettingsStore settings,
        ILogger<BuilderAgent> logger)
        : base(factory, configStore, promptStore, logger)
    {
        _settings = settings;
    }

    public override async Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
    {
        var outputRoot = ctx.OutputRoot
                      ?? ctx.ProjectPath
                      ?? PathHelper.GetProjectOutputRoot(ctx.ProjectId);
        PathHelper.EnsureDirectory(outputRoot);
        ctx.OutputRoot = outputRoot;

        Logger.LogInformation("[Builder] Рабочая папка: {Path}", outputRoot);

        // Один бэкап на прогон (важно для цикла FixError ↔ Builder).
        if (ctx.Mode != WorkMode.CreateNew
            && ctx.ProjectPath is not null
            && !ctx.SharedData.ContainsKey("backup_path"))
        {
            var backupFolder = PathHelper.CreateBackupFolder();
            var actualBackup = BackupService.Backup(ctx.ProjectPath, backupFolder);
            ctx.SharedData["backup_path"] = actualBackup;
            Logger.LogInformation("[Builder] Бэкап: {Path}", actualBackup);
        }

        int filesWritten = await WriteCoderFilesAsync(ctx, outputRoot, ct);
        Logger.LogInformation("[Builder] Записано файлов: {Count}", filesWritten);

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

    private async Task<int> WriteCoderFilesAsync(AgentContext ctx, string outputRoot, CancellationToken ct)
    {
        int filesWritten = 0;
        var coderMode = _settings.GetCoderMode();
        string[] keys = coderMode == CoderMode.Unified
            ? new[] { "fullstack_code", "game_code", "tests_code", "fix_code" }
            : new[] { "backend_code", "frontend_code", "game_code", "tests_code", "fix_code" };

        foreach (var key in keys)
        {
            if (!ctx.SharedData.TryGetValue(key, out var blob)) continue;

            foreach (var b in CodeExtractor.Extract(blob))
            {
                if (b.Path is null) continue;
                if (!FileWriteGuard.TryResolve(outputRoot, b.Path, out var full, out var deny))
                {
                    Logger.LogWarning("[Builder] Запись отклонена ({Path}): {Reason}", b.Path, deny);
                    continue;
                }

                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) PathHelper.EnsureDirectory(dir);

                if (b.IsDiff && b.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                {
                    await CsprojDiffApplier.ApplyAsync(full, b.Code, ct);
                }
                else
                {
                    await File.WriteAllTextAsync(full, b.Code, ct);
                }
                filesWritten++;
            }
        }
        return filesWritten;
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
