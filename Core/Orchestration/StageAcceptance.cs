using Build;
using Core.Contracts;
using Core.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Orchestration;

public sealed record AcceptanceResult(bool Ok, string Summary, string Log);

/// <summary>
/// Чеклист приёмки этапа после успешной сборки: tests + smoke run для MonoGame.
/// </summary>
public static class StageAcceptance
{
    public static async Task<AcceptanceResult> RunAsync(
        AgentContext ctx,
        Stage stage,
        string repo,
        CancellationToken ct)
    {
        var log = new System.Text.StringBuilder();
        bool ok = true;

        // 1) Tests — если scope Tests или есть тестовый проект
        bool wantsTests = stage.Scope.Any(s =>
            s.Equals("Tests", StringComparison.OrdinalIgnoreCase));
        var testProjects = NuGetPackageResolver.FindProjectFiles(repo)
            .Where(p => Path.GetFileNameWithoutExtension(p)
                .Contains("Test", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (wantsTests || testProjects.Count > 0)
        {
            var (code, so, se) = await DotnetRunner.TestAsync(repo, ct);
            log.AppendLine("=== dotnet test ===");
            log.AppendLine(so);
            log.AppendLine(se);
            ctx.SharedData["test_ok"] = (code == 0).ToString().ToLowerInvariant();
            ctx.SharedData["test_log"] = so + "\n" + se;
            if (code != 0)
            {
                ok = false;
                log.AppendLine("TEST FAILED");
            }
        }
        else
        {
            ctx.SharedData["test_ok"] = "skipped";
        }

        // 2) MonoGame smoke — короткий dotnet run с таймаутом
        bool isGame = ctx.Type == ProjectType.MonogameGame
            || stage.Scope.Any(s => s.Equals("Game", StringComparison.OrdinalIgnoreCase));

        if (ok && isGame)
        {
            var gameCsproj = NuGetPackageResolver.FindProjectFiles(repo)
                .FirstOrDefault(p =>
                {
                    try
                    {
                        return File.ReadAllText(p).Contains("MonoGame", StringComparison.OrdinalIgnoreCase);
                    }
                    catch { return false; }
                }) ?? NuGetPackageResolver.FindProjectFiles(repo).FirstOrDefault();

            if (gameCsproj is not null)
            {
                var (smokePassed, smokeLog) = await DotnetRunner.SmokeRunAsync(
                    gameCsproj, TimeSpan.FromSeconds(12), ct);
                log.AppendLine("=== monoGame smoke (dotnet run, 12s) ===");
                log.AppendLine(smokeLog);
                ctx.SharedData["smoke_ok"] = smokePassed.ToString().ToLowerInvariant();
                ctx.SharedData["smoke_log"] = smokeLog;

                // Smoke: успех = процесс стартовал и либо ещё жив после таймаута (убили мы),
                // либо вышел с 0. Падение сразу с ненулевым кодом — fail.
                if (!smokePassed)
                {
                    ok = false;
                    log.AppendLine("SMOKE FAILED");
                }
            }
        }
        else
        {
            ctx.SharedData["smoke_ok"] = "skipped";
        }

        ctx.SharedData["acceptance_ok"] = ok.ToString().ToLowerInvariant();
        ctx.SharedData["acceptance_log"] = log.ToString();

        ctx.SharedData.TryGetValue("test_ok", out var testStatus);
        ctx.SharedData.TryGetValue("smoke_ok", out var smokeStatus);
        var summary = ok
            ? $"acceptance OK (test={testStatus ?? "?"}, smoke={smokeStatus ?? "?"})"
            : $"acceptance FAILED (test={testStatus ?? "?"}, smoke={smokeStatus ?? "?"})";

        return new AcceptanceResult(ok, summary, log.ToString());
    }
}
