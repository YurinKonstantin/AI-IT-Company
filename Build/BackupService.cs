using System;
using System.IO;

namespace Build;

/// <summary>Простой бэкап папки проекта перед Improve/Fix.</summary>
public static class BackupService
{
    public static string Backup(string sourceDir, string backupFolder)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException(sourceDir);

        Directory.CreateDirectory(backupFolder);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var dest = Path.Combine(backupFolder, Path.GetFileName(sourceDir.TrimEnd('\\', '/')) + "_" + stamp);
        CopyDirectory(sourceDir, dest);
        return dest;
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
        {
            var name = Path.GetFileName(file);
            if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(dst, name), overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(src))
        {
            var name = Path.GetFileName(dir);
            if (name is "bin" or "obj" or ".git" or ".vs") continue;
            CopyDirectory(dir, Path.Combine(dst, name));
        }
    }
}
