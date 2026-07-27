using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Build;

public static class DotnetRunner
{
    public static async Task<(int Code, string Out, string Err)> RunAsync(
        string args, string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet", args)
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