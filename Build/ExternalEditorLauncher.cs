using System;
using System.Diagnostics;
using System.IO;

namespace Build;

public static class ExternalEditorLauncher
{
    /// <summary>
    /// Открывает файл во внешнем редакторе. argsTemplate: например code -g "{file}:{line}"
    /// Если editorPath пуст — explorer /select.
    /// </summary>
    public static bool Open(string filePath, string? editorPath, string? argsTemplate, int? line = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            if (string.IsNullOrWhiteSpace(editorPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
                {
                    UseShellExecute = true
                });
                return true;
            }

            var args = (argsTemplate ?? "\"{file}\"")
                .Replace("{file}", filePath, StringComparison.OrdinalIgnoreCase)
                .Replace("{line}", (line ?? 1).ToString(), StringComparison.OrdinalIgnoreCase);

            Process.Start(new ProcessStartInfo(editorPath, args)
            {
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
