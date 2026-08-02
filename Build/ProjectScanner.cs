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
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".csproj", ".sln", ".md", ".json", ".xml",
        ".props", ".targets", ".resw", ".mgcb", ".yml", ".yaml",
        ".config", ".razor", ".html", ".css", ".editorconfig",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".svg", ".ico"
    };

    private static readonly string[] SkipDirNames =
    {
        "bin", "obj", "Backup", ".git", "node_modules", "packages",
        ".vs", ".idea", "TestResults", "artifacts"
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

        void Walk(string dir, int depth)
        {
            if (depth > 12) return;

            IEnumerable<string> subDirs;
            try { subDirs = Directory.EnumerateDirectories(dir); }
            catch { return; }

            foreach (var sub in subDirs)
            {
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
                var ext = Path.GetExtension(f);
                if (SourceExtensions.Contains(ext) || string.IsNullOrEmpty(ext) && IsDotFile(f))
                    files.Add(f);
            }
        }

        Walk(root, 0);
        files.Sort(StringComparer.OrdinalIgnoreCase);
        folders.Sort(StringComparer.OrdinalIgnoreCase);

        var type = DetectType(files);
        var tree = BuildStructureTree(root, folders, files);
        return new ProjectScanResult(type, files, folders, tree);
    }

    private static bool IsDotFile(string path)
    {
        var name = Path.GetFileName(path);
        return name is ".gitignore" or ".dockerignore" or ".gitattributes";
    }

    private static string DetectType(List<string> files)
    {
        var csproj = files.FirstOrDefault(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        if (csproj is null) return "Unknown";

        string txt;
        try { txt = File.ReadAllText(csproj); }
        catch { return "Unknown"; }

        if (txt.Contains("MonoGame", StringComparison.OrdinalIgnoreCase)) return "MonogameGame";
        if (txt.Contains("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase)) return "WinUI";
        if (txt.Contains("Microsoft.Maui", StringComparison.OrdinalIgnoreCase)
            || txt.Contains("UseMaui", StringComparison.OrdinalIgnoreCase)) return "Maui";
        if (txt.Contains("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase)) return "Api";
        if (txt.Contains("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase)
            || txt.Contains("Microsoft.Extensions.Hosting.WindowsServices", StringComparison.OrdinalIgnoreCase)
            || txt.Contains("UseWindowsService", StringComparison.OrdinalIgnoreCase))
            return "WindowsService";

        return "Console";
    }

    private static string BuildStructureTree(string root, List<string> folders, List<string> files)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + "/");

        var relFolders = folders
            .Select(f => Path.GetRelativePath(root, f))
            .Where(r => !r.StartsWith(".."))
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();

        foreach (var rel in relFolders)
        {
            var depth = rel.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);
            sb.Append(new string(' ', (depth + 1) * 2));
            sb.AppendLine(Path.GetFileName(rel) + "/");
        }

        sb.AppendLine();
        sb.AppendLine($"Files ({files.Count}):");
        foreach (var f in files.Take(400))
        {
            var rel = Path.GetRelativePath(root, f);
            if (rel.StartsWith("..")) continue;
            sb.AppendLine("  " + rel.Replace('\\', '/'));
        }
        if (files.Count > 400)
            sb.AppendLine($"  ... and {files.Count - 400} more");

        return sb.ToString();
    }

    /// <summary>
    /// Читает текстовые превью ключевых файлов для промпта документатора.
    /// </summary>
    public static string BuildFilePreviews(string root, IEnumerable<string> files, int maxFiles = 28, int maxCharsPerFile = 2500, int maxTotalChars = 40000)
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
