
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

namespace Core.Agents
{
    public sealed class BuilderAgent : AgentBase
    {
        public override AgentRole Role => AgentRole.Builder;
        protected override string SystemPrompt => "Sink-агент, LLM не используется активно.";

        public BuilderAgent(IAiProviderFactory factory, AgentConfigStore configStore,
                    ILogger<BuilderAgent> logger) : base(factory, configStore, logger) { }

        public override async Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
        {
            var outputRoot = ctx.ProjectPath ?? Path.Combine("Output", ctx.ProjectId);
            Directory.CreateDirectory(outputRoot);

            if (ctx.Mode != WorkMode.CreateNew && ctx.ProjectPath != null)
                BackupService.Backup(ctx.ProjectPath);

            foreach (var key in new[] { "backend_code", "frontend_code", "game_code", "tests_code", "fix_code" })
            {
                if (!ctx.SharedData.TryGetValue(key, out var blob)) continue;

                // Переименовали 'code' → 'content', чтобы не конфликтовало с exitCode ниже.
                foreach (var (_, path, content) in CodeExtractor.Extract(blob))
                {
                    if (path is null) continue;
                    var full = Path.Combine(outputRoot, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    await File.WriteAllTextAsync(full, content, ct);
                }
            }

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
