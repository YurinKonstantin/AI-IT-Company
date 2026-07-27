using System;
using System.IO;

namespace Build;

public static class BackupService
{
    public static string Backup(string projectPath)
    {
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var dst = Path.Combine("Backup", ts);
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(projectPath, "*.*", SearchOption.AllDirectories))
        {
            if (f.Contains("\\bin\\") || f.Contains("\\obj\\")) continue;
            var rel = Path.GetRelativePath(projectPath, f);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, true);
        }
        return dst;
    }
}