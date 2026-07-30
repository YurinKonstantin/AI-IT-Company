using System;
using System.IO;
using System.Linq;

namespace Build;

/// <summary>
/// Копирует содержимое проекта в указанную папку бэкапа.
/// Не зависит от PathHelper — путь назначения передаёт вызывающая сторона.
/// </summary>
public static class BackupService
{
    /// <summary>
    /// Копирует всё из <paramref name="sourceProjectPath"/> в <paramref name="destinationFolder"/>,
    /// пропуская bin/obj и (по желанию) .git.
    /// </summary>
    /// <returns>Полный путь к созданной папке бэкапа.</returns>
    public static string Backup(string sourceProjectPath, string destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(sourceProjectPath))
            throw new ArgumentException("Не указан путь исходного проекта.", nameof(sourceProjectPath));

        if (!Directory.Exists(sourceProjectPath))
            throw new DirectoryNotFoundException($"Папка проекта не найдена: {sourceProjectPath}");

        if (string.IsNullOrWhiteSpace(destinationFolder))
            throw new ArgumentException("Не указан путь назначения.", nameof(destinationFolder));

        Directory.CreateDirectory(destinationFolder);

        var sep = Path.DirectorySeparatorChar;
        var skipMarkers = new[]
        {
            $"{sep}bin{sep}",
            $"{sep}obj{sep}",
            $"{sep}.git{sep}",
            $"{sep}.vs{sep}"
        };

        int filesCopied = 0;
        foreach (var file in Directory.EnumerateFiles(sourceProjectPath, "*.*", SearchOption.AllDirectories))
        {
            if (skipMarkers.Any(m => file.Contains(m, StringComparison.OrdinalIgnoreCase)))
                continue;

            var relative = Path.GetRelativePath(sourceProjectPath, file);
            var target = Path.Combine(destinationFolder, relative);
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            File.Copy(file, target, overwrite: true);
            filesCopied++;
        }

        return destinationFolder;
    }
}