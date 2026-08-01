using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AI_IT_Company.ViewModels
{
    public sealed partial class LogFileItem : ObservableObject
    {
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";
        public DateTime Modified { get; init; }
        public long SizeBytes { get; init; }
        public string DisplaySize =>
            SizeBytes < 1024 ? $"{SizeBytes} B"
            : SizeBytes < 1024 * 1024 ? $"{SizeBytes / 1024} KB"
            : $"{SizeBytes / (1024 * 1024)} MB";
    }

    public partial class LogsViewModel : ObservableObject, IDisposable
    {
        private readonly DispatcherQueue _ui;
        private FileSystemWatcher? _watcher;
        private CancellationTokenSource? _autoReloadCts;

        public ObservableCollection<LogFileItem> Files { get; } = new();

        [ObservableProperty] private LogFileItem? selectedFile;
        [ObservableProperty] private string content = "";
        [ObservableProperty] private bool autoRefresh = true;
        [ObservableProperty] private string statusText = "";
        [ObservableProperty] private long contentLength;

        public string LogsRoot { get; }

        public LogsViewModel()
        {
            _ui = DispatcherQueue.GetForCurrentThread();
            LogsRoot = PathHelper.LogsRoot; // используем тот же helper, что и Serilog
            Directory.CreateDirectory(LogsRoot);
            Refresh();
            StartWatcher();
        }

        // ===== Commands =====

        [RelayCommand]
        public void Refresh()
        {
            try
            {
                var dir = new DirectoryInfo(LogsRoot);
                var current = SelectedFile ?.Path;
                Files.Clear();
                foreach (var f in dir.EnumerateFiles("*.log").OrderByDescending(x => x.LastWriteTime))
                {
                    Files.Add(new LogFileItem
                    {
                        Name = f.Name,
                        Path = f.FullName,
                        Modified = f.LastWriteTime,
                        SizeBytes = f.Length
                    });
                }
                StatusText = $"Файлов: {Files.Count}";
                SelectedFile = Files.FirstOrDefault(f => f.Path == current) ?? Files.FirstOrDefault();
            }
            catch (Exception ex)
            {
                StatusText = "Ошибка: " + ex.Message;
            }
        }

        [RelayCommand]
        private void OpenFolder()
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogsRoot}\"") { UseShellExecute = true });
            }
            catch { }
        }

        [RelayCommand]
        private void CopyContent()
        {
            try
            {
                var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
                pkg.SetText(Content ?? "");
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
                StatusText = "📋 Скопировано в буфер обмена";
            }
            catch { }
        }

        [RelayCommand]
        private void ClearContent()
        {
            Content = "";
            ContentLength = 0;
        }

        // ===== Reactive =====

        partial void OnSelectedFileChanged(LogFileItem? value)
        {
            LoadContent(value?.Path);
        }

        partial void OnAutoRefreshChanged(bool value)
        {
            if (value) StartAutoReload();
            else StopAutoReload();
        }

        // ===== Internal =====

        private void LoadContent(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Content = "";
                ContentLength = 0;
                return;
            }
            try
            {
                // Файл может быть открыт Serilog'ом на запись — читаем с ShareReadWrite.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                var text = sr.ReadToEnd();

                // Ограничим объём: показываем последние 200 000 символов, иначе UI задохнётся.
                const int max = 200_000;
                if (text.Length > max) text = "…(начало обрезано)\n" + text[^max..];
                Content = text;
                ContentLength = new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                Content = "Не удалось прочитать файл: " + ex.Message;
            }
        }

        private void StartWatcher()
        {
            try
            {
                _watcher = new FileSystemWatcher(LogsRoot, "*.log")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size |
                                   NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };
                _watcher.Changed += OnFileChanged;
                _watcher.Created += OnFileChanged;
                _watcher.Renamed += (_, _) => _ui.TryEnqueue(Refresh);
                StartAutoReload();
            }
            catch { /* каталог логов может быть недоступен — не критично */ }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!AutoRefresh) return;
            _ui.TryEnqueue(() =>
            {
                // обновим только контент выбранного файла (если он изменился)
                if (SelectedFile?.Path == e.FullPath) LoadContent(e.FullPath);
            });
        }

        private void StartAutoReload()
        {
            StopAutoReload();
            _autoReloadCts = new CancellationTokenSource();
            var token = _autoReloadCts.Token;

            // На всякий случай раз в 2 секунды переопрашиваем — не все редакторы шлют FSW-события.
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(2000, token);
                        _ui.TryEnqueue(() =>
                        {
                            if (AutoRefresh && SelectedFile is not null)
                                LoadContent(SelectedFile.Path);
                        });
                    }
                }
                catch (OperationCanceledException) { }
            }, token);
        }

        private void StopAutoReload()
        {
            try { _autoReloadCts?.Cancel(); } catch { }
            _autoReloadCts?.Dispose();
            _autoReloadCts = null;
        }

        public void Dispose()
        {
            StopAutoReload();
            _watcher?.Dispose();
        }
    }
}
