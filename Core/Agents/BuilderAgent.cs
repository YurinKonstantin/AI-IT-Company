
using Build;
using Core.Configuration;
using Core.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents
{
    public sealed class BuilderAgent : AgentBase
    {
        public override AgentRole Role => AgentRole.Builder;
        protected override string DefaultSystemPrompt => "Sink-agent, LLM is not actively used.";
        private readonly Configuration.AppSettingsStore _settings;

        public BuilderAgent(IAiProviderFactory factory, AgentConfigStore configStore, AgentPromptStore promptStore, Configuration.AppSettingsStore settings,
                    ILogger<BuilderAgent> logger) : base(factory, configStore, promptStore, logger) { _settings = settings; }

        public override async Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
        {
            // Определяем корень проекта — один раз.
            var outputRoot = ctx.OutputRoot
                          ?? ctx.ProjectPath
                          ?? PathHelper.GetProjectOutputRoot(ctx.ProjectId);
            PathHelper.EnsureDirectory(outputRoot);
            ctx.OutputRoot = outputRoot;

            Logger.LogInformation("[Builder] Рабочая папка: {Path}", outputRoot);

            // Бэкап, если работаем с существующим проектом.
            if (ctx.Mode != WorkMode.CreateNew && ctx.ProjectPath is not null)
            {
                var backupFolder = PathHelper.CreateBackupFolder();   // Core знает PathHelper
                var actualBackup = BackupService.Backup(ctx.ProjectPath, backupFolder); // Build — просто копирует
                ctx.SharedData["backup_path"] = actualBackup;
                Logger.LogInformation("[Builder] Бэкап: {Path}", actualBackup);
            }

            // Пишем файлы, полученные от кодеров и исправителя.
            int filesWritten = 0;
            var coderMode = _settings.GetCoderMode();
            string[] masRole = new[] { "backend_code", "frontend_code", "game_code", "tests_code", "fix_code" };
            if (coderMode == CoderMode.Unified)
            {
                masRole = new[] { "fullstack_code", "game_code", "tests_code", "fix_code" };
            }
            foreach (var key in masRole)
            {
               
                    if (!ctx.SharedData.TryGetValue(key, out var blob)) continue;

                    foreach (var b in CodeExtractor.Extract(blob))
                    {
                        if (b.Path is null) continue;
                        var full = Path.Combine(outputRoot, b.Path);
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
            Logger.LogInformation("[Builder] Записано файлов: {Count}", filesWritten);

            // Собираем проект.
            var (exitCode, stdOut, stdErr) = await DotnetRunner.RunAsync("build", outputRoot, ct);
            ctx.SharedData["build_ok"] = (exitCode == 0).ToString().ToLower();
            ctx.SharedData["build_log"] = stdOut + "\n" + stdErr;

            return new AgentResult(
                Success: exitCode == 0,
                Output: stdOut,
                Error: exitCode == 0 ? null : stdErr);
        }

        protected override string BuildUserPrompt(AgentContext ctx) => "";
    }
}
