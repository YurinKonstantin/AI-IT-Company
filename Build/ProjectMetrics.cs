using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Build;

/// <summary>
/// Лёгкие метрики существующего проекта для режима Analyze.
/// </summary>
public static class ProjectMetrics
{
    public static string CollectReport(string root, IReadOnlyList<string> files)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Root: {root}");
        sb.AppendLine($"Scanned files: {files.Count}");

        var byExt = files
            .GroupBy(f => Path.GetExtension(f).ToLowerInvariant())
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key);

        sb.AppendLine("By extension:");
        foreach (var g in byExt)
            sb.AppendLine($"  {g.Key}: {g.Count()}");

        var csFiles = files.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
        long totalLines = 0;
        var largest = new List<(string Path, int Lines)>();

        foreach (var f in csFiles)
        {
            try
            {
                var lines = File.ReadAllLines(f).Length;
                totalLines += lines;
                largest.Add((Path.GetRelativePath(root, f).Replace('\\', '/'), lines));
            }
            catch { /* ignore */ }
        }

        sb.AppendLine($"C# files: {csFiles.Count}");
        sb.AppendLine($"C# lines (approx): {totalLines}");
        sb.AppendLine("Largest .cs files:");
        foreach (var (path, lines) in largest.OrderByDescending(x => x.Lines).Take(12))
            sb.AppendLine($"  {lines,6}  {path}");

        var testFiles = csFiles.Count(f =>
            Path.GetFileName(f).Contains("Test", StringComparison.OrdinalIgnoreCase)
            || f.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        sb.AppendLine($"Likely test files: {testFiles}");

        var csproj = files.FirstOrDefault(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        if (csproj is not null)
        {
            try
            {
                var txt = File.ReadAllText(csproj);
                var pkgs = Regex.Matches(txt, @"PackageReference\s+[^>]*Include\s*=\s*""([^""]+)""",
                    RegexOptions.IgnoreCase);
                sb.AppendLine($"Primary csproj: {Path.GetRelativePath(root, csproj).Replace('\\', '/')}");
                sb.AppendLine($"PackageReference count: {pkgs.Count}");
                foreach (Match m in pkgs.Cast<Match>().Take(30))
                    sb.AppendLine($"  - {m.Groups[1].Value}");
            }
            catch { /* ignore */ }
        }

        bool hasReadme = files.Any(f =>
            Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase));
        bool hasSlf = files.Any(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        sb.AppendLine($"Has README.md: {hasReadme}");
        sb.AppendLine($"Has .sln: {hasSlf}");

        return sb.ToString();
    }
}
