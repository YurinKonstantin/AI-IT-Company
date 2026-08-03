using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Build;

public static class DotnetRunner
{
    /// <summary>Опциональный sink для ленты событий (команда + stdout/stderr).</summary>
    public static Action<string>? TerminalSink { get; set; }

    public static async Task<(int Code, string Out, string Err)> RunAsync(
        string args, string workingDir, CancellationToken ct)
    {
        var cmd = $"dotnet {args}";
        TerminalSink?.Invoke($"$ {cmd}  (cwd: {workingDir})");

        var psi = CreateDotnetStartInfo(args, workingDir);
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync(ct);
        var se = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var stdout = await so;
        var stderr = await se;

        if (!string.IsNullOrWhiteSpace(stdout))
            TerminalSink?.Invoke(Truncate(stdout.TrimEnd(), 2500));
        if (!string.IsNullOrWhiteSpace(stderr))
            TerminalSink?.Invoke("[stderr]\n" + Truncate(stderr.TrimEnd(), 1500));
        TerminalSink?.Invoke($"exit {p.ExitCode}");

        return (p.ExitCode, stdout, stderr);
    }

    private static ProcessStartInfo CreateDotnetStartInfo(string args, string workingDir)
    {
        var psi = new ProcessStartInfo("dotnet", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // UTF-8 so MSBuild messages aren't mojibake on non-English Windows.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Force English CLI / MSBuild messages for reliable ErrorFixer parsing.
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        psi.Environment["VSLANG"] = "1033";
        psi.Environment["PreferredUILang"] = "en-US";

        return psi;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    public static Task<(int Code, string Out, string Err)> RestoreAsync(
        string workingDir, CancellationToken ct)
        => RunAsync("restore --nologo", workingDir, ct);

    public static Task<(int Code, string Out, string Err)> BuildAsync(
        string workingDir, CancellationToken ct)
        => RunAsync("build --nologo", workingDir, ct);

    public static Task<(int Code, string Out, string Err)> TestAsync(
        string workingDir, CancellationToken ct)
        => RunAsync("test --nologo --verbosity minimal", workingDir, ct);

    /// <summary>
    /// Короткий smoke: запускает проект и через timeout убивает процесс.
    /// Успех: процесс дожил до timeout (типично для GUI) ИЛИ вышел с кодом 0.
    /// Провал: быстрый ненулевой exit.
    /// </summary>
    public static async Task<(bool Ok, string Log)> SmokeRunAsync(
        string csprojPath,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var workingDir = System.IO.Path.GetDirectoryName(csprojPath)
                      ?? Environment.CurrentDirectory;
        var args = $"run --project \"{csprojPath}\" --nologo";

        var psi = CreateDotnetStartInfo(args, workingDir);

        using var p = Process.Start(psi)!;
        var sbOut = new StringBuilder();
        var sbErr = new StringBuilder();

        var readOut = Task.Run(async () =>
        {
            var t = await p.StandardOutput.ReadToEndAsync(ct);
            sbOut.Append(t);
        }, ct);
        var readErr = Task.Run(async () =>
        {
            var t = await p.StandardError.ReadToEndAsync(ct);
            sbErr.Append(t);
        }, ct);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        try
        {
            await p.WaitForExitAsync(linked.Token);
            try { await Task.WhenAll(readOut, readErr); } catch { /* ignore */ }

            var log = sbOut + "\n" + sbErr + $"\n[exit={p.ExitCode}]";
            return (p.ExitCode == 0, log);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try
            {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }

            try { await Task.WhenAll(readOut, readErr); } catch { /* ignore */ }
            var log = sbOut + "\n" + sbErr +
                      $"\n[smoke-timeout {timeout.TotalSeconds:0}s — process was running, treated as OK]";
            return (true, log);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            return (false, "smoke cancelled");
        }
    }

    /// <summary>
    /// Добавляет NuGet-пакет в проект: dotnet add &lt;csproj&gt; package &lt;id&gt; [--version].
    /// </summary>
    public static async Task<(bool Ok, string Log)> AddPackageAsync(
        string csprojPath,
        string packageId,
        string? version,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(csprojPath) || string.IsNullOrWhiteSpace(packageId))
            return (false, "csproj or package id is empty");

        var workingDir = System.IO.Path.GetDirectoryName(csprojPath)
                      ?? Environment.CurrentDirectory;

        var sb = new StringBuilder();
        sb.Append("add \"").Append(csprojPath).Append("\" package ").Append(packageId);
        if (!string.IsNullOrWhiteSpace(version))
            sb.Append(" --version ").Append(version);

        var (code, so, se) = await RunAsync(sb.ToString(), workingDir, ct);
        var log = so + "\n" + se;
        return (code == 0, log);
    }
}
