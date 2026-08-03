using System;
using System.IO;

namespace Core;

/// <summary>
/// Единая точка правды о путях приложения.
/// Корень: %USERPROFILE%\AiItCompany\ (видимая, не скрытая папка).
/// Структура:
///   C:\Users\{User}\AiItCompany\
///     ├── Output\          — сгенерированные проекты
///     ├── Backup\          — резервные копии
///     ├── Logs\            — журналы Serilog
///     ├── Models\          — локальные модели (на будущее)
///     └── aiitcompany.db   — SQLite
/// </summary>
public static class PathHelper
{
    /// <summary>Корневая папка приложения (создаётся автоматически).</summary>
    public static string AppRoot { get; } = Init(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AiItCompany"));

    public static string OutputRoot { get; } = Init(Path.Combine(AppRoot, "Output"));
    public static string BackupRoot { get; } = Init(Path.Combine(AppRoot, "Backup"));
    public static string LogsRoot { get; } = Init(Path.Combine(AppRoot, "Logs"));
    public static string LearningRoot { get; } = Init(Path.Combine(AppRoot, "Learning"));
    public static string ModelsRoot { get; } = Init(Path.Combine(AppRoot, "Models"));
    public static string DatabasePath { get; } = Path.Combine(AppRoot, "aiitcompany.db");

    /// <summary>Папка конкретного сгенерированного проекта.</summary>
    public static string GetProjectOutputRoot(string projectId)
        => Init(Path.Combine(OutputRoot, projectId));

    /// <summary>Папка бэкапа с временным штампом.</summary>
    public static string CreateBackupFolder()
    {
        var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return Init(Path.Combine(BackupRoot, ts));
    }

    /// <summary>Создаёт папку (если её нет) и возвращает её путь.</summary>
    private static string Init(string path)
    {
        try { Directory.CreateDirectory(path); }
        catch { /* silently ignore — упадём при первом реальном обращении с внятной ошибкой */ }
        return path;
    }

    /// <summary>Гарантирует существование папки, кидает понятную ошибку, если не удалось.</summary>
    public static string EnsureDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Не удалось создать директорию '{path}': {ex.Message}", ex);
        }
    }
}