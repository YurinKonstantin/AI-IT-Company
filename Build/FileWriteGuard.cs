using System;
using System.IO;
using System.Linq;

namespace Build;

/// <summary>
/// Ограничивает запись файлов агентами: только внутри корня проекта,
/// без .git/bin/obj и без выхода через "..".
/// </summary>
public static class FileWriteGuard
{
    private static readonly string[] BlockedSegments =
    {
        ".git", "bin", "obj", ".vs", ".idea", "node_modules",
        "packages", "Backup", "TestResults", "artifacts"
    };

    public static bool TryResolve(string projectRoot, string relativePath, out string fullPath, out string denyReason)
    {
        fullPath = "";
        denyReason = "";

        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(relativePath))
        {
            denyReason = "empty root or path";
            return false;
        }

        var rel = relativePath.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(rel))
        {
            denyReason = "rooted path not allowed";
            return false;
        }

        if (rel.Contains("..", StringComparison.Ordinal))
        {
            denyReason = "path traversal (..)";
            return false;
        }

        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(p => p.Length > 0)
            .ToArray();

        foreach (var part in parts)
        {
            if (BlockedSegments.Any(b => part.Equals(b, StringComparison.OrdinalIgnoreCase)))
            {
                denyReason = $"blocked segment: {part}";
                return false;
            }
        }

        var rootFull = Path.GetFullPath(projectRoot);
        var candidate = Path.GetFullPath(Path.Combine(rootFull, rel));

        var rootPrefix = rootFull.TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            && !candidate.Equals(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            denyReason = "outside project root";
            return false;
        }

        fullPath = candidate;
        return true;
    }
}
