using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Build;

public sealed record ProjectScanResult(
    string Type,
    List<string> Files,
    List<string> Folders,
    string StructureTree);

public static class ProjectScanner
{
    private const int MaxWalkDepth = 8;
    private const int MaxSourceFiles = 800;

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".csproj", ".sln", ".md", ".json", ".xml",
        ".props", ".targets", ".resw", ".mgcb", ".yml", ".yaml",
        ".config", ".razor", ".html", ".css", ".editorconfig"
    };

    private static readonly string[] SkipDirNames =
    {
        "bin", "obj", "Backup", ".git", "node_modules", "packages",
        ".vs", ".idea", "TestResults", "artifacts", "publish", "dist",
        ".nuget", "Coverage", ".cursor", ".vscode", "out", "Debug", "Release"
    };

    public static (string Type, List<string> Files) Scan(string root)
    {
        var r = ScanDetailed(root);
        return (r.Type, r.Files);
    }

    public static ProjectScanResult ScanDetailed(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new ProjectScanResult("Unknown", new List<string>(), new List<string>(), "(path missing)");

        var files = new List<string>();
        var folders = new List<string>();
        var hitCap = false;

        void Walk(string dir, int depth)
        {
            if (hitCap || depth > MaxWalkDepth) return;

            IEnumerable<string> subDirs;
            try { subDirs = Directory.EnumerateDirectories(dir); }
            catch { return; }

            foreach (var sub in subDirs)
            {
                if (hitCap) break;
                var name = Path.GetFileName(sub);
                if (SkipDirNames.Any(s => name.Equals(s, StringComparison.OrdinalIgnoreCase)))
                    continue;
                folders.Add(sub);
                Walk(sub, depth + 1);
            }

            IEnumerable<string> localFiles;
            try { localFiles = Directory.EnumerateFiles(dir); }
            catch { return; }

            foreach (var f in localFiles)
            {
                if (files.Count >= MaxSourceFiles)
                {
                    hitCap = true;
                    break;
                }

                var ext = Path.GetExtension(f);
                if (SourceExtensions.Contains(ext) || string.IsNullOrEmpty(ext) && IsDotFile(f))
                    files.Add(f);
            }
        }

        Walk(root, 0);
        files.Sort(StringComparer.OrdinalIgnoreCase);
        folders.Sort(StringComparer.OrdinalIgnoreCase);

        var type = DetectType(files, root);
        var tree = BuildStructureTree(root, folders, files, hitCap);
        return new ProjectScanResult(type, files, folders, tree);
    }

    private static bool IsDotFile(string path)
    {
        var name = Path.GetFileName(path);
        return name is ".gitignore" or ".dockerignore" or ".gitattributes";
    }

    /// <summary>
    /// Scores all .csproj files and picks the strongest app type (WinUI/MonoGame/… over Console libs).
    /// </summary>
    private static string DetectType(List<string> files, string root)
    {
        var csprojs = files
            .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (csprojs.Count == 0) return "Unknown";

        string bestType = "Unknown";
        int bestScore = -1;
        var rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        foreach (var csproj in csprojs)
        {
            string txt;
            try { txt = File.ReadAllText(csproj); }
            catch { continue; }

            var (type, score) = ScoreCsproj(csproj, txt, rootName);
            if (score > bestScore)
            {
                bestScore = score;
                bestType = type;
            }
        }

        return bestType == "Unknown" ? "Console" : bestType;
    }

    private static (string Type, int Score) ScoreCsproj(string csprojPath, string txt, string? rootName)
    {
        var fileName = Path.GetFileNameWithoutExtension(csprojPath);
        int bonus = 0;
        if (!string.IsNullOrEmpty(rootName)
            && fileName.Equals(rootName, StringComparison.OrdinalIgnoreCase))
            bonus += 5;
        if (txt.Contains("OutputType>WinExe", StringComparison.OrdinalIgnoreCase)
            || txt.Contains("OutputType>Exe", StringComparison.OrdinalIgnoreCase))
            bonus += 3;
        // Prefer app projects over class libraries.
        if (txt.Contains("OutputType>Library", StringComparison.OrdinalIgnoreCase)
            || (!txt.Contains("OutputType", StringComparison.OrdinalIgnoreCase)
                && txt.Contains("Microsoft.NET.Sdk\"", StringComparison.Ordinal)))
            bonus -= 2;

        if (txt.Contains("MonoGame", StringComparison.OrdinalIgnoreCase))
            return ("MonogameGame", 100 + bonus);
        if (txt.Contains("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase)
            || txt.Contains("UseWinUI", StringComparison.OrdinalIgnoreCase)
            || txt.Contains("Microsoft.WinUI", StringComparison.OrdinalIgnoreCase))
            return ("WinUI", 90 + bonus);
        if (txt.Contains("Microsoft.Maui", StringComparison.OrdinalIgnoreCase)
            || txt.Contains("UseMaui", StringComparison.OrdinalIgnoreCase))
            return ("Maui", 85 + bonus);
        if (txt.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase)
            || txt.Contains("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase))
            return ("Api", 80 + bonus);
        if (txt.Contains("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase)
            || txt.Contains("Microsoft.Extensions.Hosting.WindowsServices", StringComparison.OrdinalIgnoreCase)
            || txt.Contains("UseWindowsService", StringComparison.OrdinalIgnoreCase))
            return ("WindowsService", 75 + bonus);

        // Generic SDK / console
        return ("Console", 20 + bonus);
    }

    private static string BuildStructureTree(
        string root, List<string> folders, List<string> files, bool truncated)
    {
        var sb = new StringBuilder();
        var rootLabel = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        sb.AppendLine(rootLabel + "/");

        const int maxDepth = 4;
        const int maxLines = 250;
        int lines = 1;

        // Prefer folders that contain source; build nested tree with full relative segments.
        var relFiles = files
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(r => !r.StartsWith("..", StringComparison.Ordinal))
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var dirSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in relFiles)
        {
            var dir = Path.GetDirectoryName(rel)?.Replace('\\', '/');
            while (!string.IsNullOrEmpty(dir))
            {
                dirSet.Add(dir);
                var parent = Path.GetDirectoryName(dir)?.Replace('\\', '/');
                dir = parent;
            }
        }

        foreach (var relDir in dirSet
                     .OrderBy(d => d.Count(c => c == '/'))
                     .ThenBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var depth = relDir.Count(c => c == '/');
            if (depth >= maxDepth) continue;
            if (lines >= maxLines) break;
            sb.Append(new string(' ', (depth + 1) * 2));
            sb.AppendLine(relDir + "/");
            lines++;
        }

        sb.AppendLine();
        sb.AppendLine($"Source files ({files.Count}" + (truncated ? ", capped" : "") + "):");

        // Group files under shallow relative paths (not every asset).
        foreach (var rel in relFiles.Take(200))
        {
            if (lines >= maxLines) break;
            var depth = rel.Count(c => c == '/');
            if (depth > maxDepth) continue;
            sb.AppendLine("  " + rel);
            lines++;
        }

        if (relFiles.Count > 200 || truncated)
            sb.AppendLine("  ... (truncated)");

        // Module summary: each csproj
        var csprojs = relFiles.Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();
        if (csprojs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Projects:");
            foreach (var c in csprojs.Take(30))
                sb.AppendLine("  - " + c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Читает текстовые превью ключевых файлов для промпта документатора.
    /// </summary>
    public static string BuildFilePreviews(
        string root,
        IEnumerable<string> files,
        int maxFiles = 20,
        int maxCharsPerFile = 2000,
        int maxTotalChars = 16000)
    {
        var textExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".xaml", ".csproj", ".sln", ".md", ".json", ".xml",
            ".props", ".targets", ".resw", ".mgcb", ".yml", ".yaml",
            ".config", ".razor", ".html", ".css", ".editorconfig"
        };

        var priorityNames = new[]
        {
            "readme.md", "program.cs", "game1.cs", "app.xaml.cs", "app.xaml",
            "mainwindow.xaml", "mainwindow.xaml.cs"
        };

        var ordered = files
            .Where(f => textExt.Contains(Path.GetExtension(f)))
            .OrderBy(f =>
            {
                var name = Path.GetFileName(f).ToLowerInvariant();
                var idx = Array.IndexOf(priorityNames, name);
                if (idx >= 0) return idx;
                if (f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return 50;
                if (f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)) return 51;
                return 100;
            })
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(maxFiles)
            .ToList();

        var sb = new StringBuilder();
        int total = 0;
        foreach (var full in ordered)
        {
            if (total >= maxTotalChars) break;
            string content;
            try
            {
                content = File.ReadAllText(full);
            }
            catch
            {
                continue;
            }

            if (content.Length > maxCharsPerFile)
                content = content[..maxCharsPerFile] + "\n... (truncated)";

            var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
            var block = $"--- FILE: {rel} ---\n{content}\n\n";
            if (total + block.Length > maxTotalChars)
            {
                var remain = maxTotalChars - total;
                if (remain < 200) break;
                block = block[..remain] + "\n... (truncated)\n";
            }
            sb.Append(block);
            total += block.Length;
        }
        return sb.ToString();
    }
}
