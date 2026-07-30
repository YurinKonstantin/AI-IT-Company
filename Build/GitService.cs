using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Build
{
    public static class GitService
    {
        public static async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            try { var (c, _, _) = await RunAsync("--version", Environment.CurrentDirectory, ct); return c == 0; }
            catch { return false; }
        }

        public static async Task InitIfNeededAsync(string repoPath, CancellationToken ct = default)
        {
            if (Directory.Exists(Path.Combine(repoPath, ".git"))) return;
            await RunAsync("init", repoPath, ct);
            await RunAsync("config user.email \"agent@ai-it-company.local\"", repoPath, ct);
            await RunAsync("config user.name \"AI IT-Company\"", repoPath, ct);
            // .gitignore для .NET
            var gi = Path.Combine(repoPath, ".gitignore");
            if (!File.Exists(gi))
                await File.WriteAllTextAsync(gi,
                    "bin/\nobj/\n.vs/\n*.user\n", ct);
        }

        /// <summary>Коммит текущего состояния. Возвращает SHA или null, если коммитить нечего.</summary>
        public static async Task<string?> CommitAllAsync(string repoPath, string message, CancellationToken ct = default)
        {
            await RunAsync("add -A", repoPath, ct);
            var (code, _, _) = await RunAsync($"commit -m \"{EscapeMsg(message)}\" --allow-empty", repoPath, ct);
            if (code != 0) return null;
            var (_, sha, _) = await RunAsync("rev-parse HEAD", repoPath, ct);
            return sha.Trim();
        }

        /// <summary>Жёсткий откат к указанному SHA (или к предыдущему коммиту, если sha == null).</summary>
        public static async Task ResetHardAsync(string repoPath, string? sha, CancellationToken ct = default)
        {
            var target = string.IsNullOrWhiteSpace(sha) ? "HEAD~1" : sha!;
            await RunAsync($"reset --hard {target}", repoPath, ct);
            // Удаляем неотслеживаемые файлы (bin/obj не тронутся из-за .gitignore).
            await RunAsync("clean -fd", repoPath, ct);
        }

        public static async Task<string?> GetHeadShaAsync(string repoPath, CancellationToken ct = default)
        {
            var (code, sha, _) = await RunAsync("rev-parse HEAD", repoPath, ct);
            return code == 0 ? sha.Trim() : null;
        }

        private static string EscapeMsg(string m) => m.Replace("\"", "'");

        private static async Task<(int Code, string Out, string Err)> RunAsync(
            string args, string workingDir, CancellationToken ct)
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync(ct);
            var se = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return (p.ExitCode, await so, await se);
        }
    }
}
