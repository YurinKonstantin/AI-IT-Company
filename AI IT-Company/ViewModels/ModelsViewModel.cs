using Ai;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AI_IT_Company.ViewModels
{
    public partial class ModelsViewModel : ObservableObject
    {
        private readonly OllamaClient _client;
        private readonly DispatcherQueue _ui;
        private CancellationTokenSource? _pullCts;

        public ObservableCollection<ModelItem> InstalledModels { get; } = new();
        public ObservableCollection<SuggestedModel> Suggested { get; } = new();

        [ObservableProperty] private bool isOllamaAvailable;
        [ObservableProperty] private string statusText = "Проверка соединения…";
        [ObservableProperty] private bool isLoading;

        [ObservableProperty] private string pullModelName = "";
        [ObservableProperty] private bool isPulling;
        [ObservableProperty] private double pullProgress;
        [ObservableProperty] private string pullStatus = "";
        [ObservableProperty] private string pullStage = "";

        public ModelsViewModel(OllamaClient client)
        {
            _client = client;
            _ui = DispatcherQueue.GetForCurrentThread()
                  ?? throw new InvalidOperationException("ModelsViewModel должен создаваться на UI-потоке.");

            // Популярные модели для quick-pull (кнопки на странице)
            foreach (var m in new[]
            {
            new SuggestedModel("llama3",              "Meta • универсальная 8B",  "~4.7 GB"),
            new SuggestedModel("llama3:70b",          "Meta • большая 70B",        "~40 GB"),
            new SuggestedModel("qwen2.5-coder:7b",    "Qwen • для кода 7B",        "~4.7 GB"),
            new SuggestedModel("qwen2.5-coder:14b",   "Qwen • для кода 14B",       "~9 GB"),
            new SuggestedModel("qwen2.5-coder:32b",   "Qwen • для кода 32B",       "~19 GB"),
            new SuggestedModel("deepseek-coder-v2",   "DeepSeek • кодер",          "~8.9 GB"),
            new SuggestedModel("phi3:mini",           "Microsoft • Phi-3 mini",    "~2.3 GB"),
            new SuggestedModel("mistral",             "Mistral • 7B",              "~4.1 GB"),
            new SuggestedModel("gemma2:9b",           "Google • Gemma 2 9B",       "~5.4 GB"),
        }) Suggested.Add(m);

            _ = RefreshAsync();
        }

        [RelayCommand]
        private async Task RefreshAsync()
        {
            IsLoading = true;
            try
            {
                IsOllamaAvailable = await _client.PingAsync();
                if (!IsOllamaAvailable)
                {
                    StatusText = "❌ Ollama не запущена (http://localhost:11434). Запустите её и повторите.";
                    InstalledModels.Clear();
                    return;
                }

                var list = await _client.ListAsync();
                InstalledModels.Clear();
                foreach (var m in list.OrderBy(m => m.Name))
                    InstalledModels.Add(ModelItem.From(m));

                StatusText = $"✅ Установлено моделей: {InstalledModels.Count}";
            }
            catch (Exception ex)
            {
                StatusText = $"⚠ Ошибка: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        private void PickSuggested(SuggestedModel m)
        {
            if (m is null) return;
            PullModelName = m.Name;
        }

        [RelayCommand]
        private async Task PullAsync()
        {
            var name = (PullModelName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name)) { PullStatus = "Введите имя модели."; return; }
            if (IsPulling) return;

            IsPulling = true;
            PullProgress = 0;
            PullStage = "запуск…";
            PullStatus = $"Скачивание {name}…";
            _pullCts = new CancellationTokenSource();

            try
            {
                await foreach (var p in _client.PullAsync(name, _pullCts.Token))
                {
                    if (!string.IsNullOrEmpty(p.Error))
                    {
                        PullStatus = $"❌ {p.Error}";
                        break;
                    }

                    _ui.TryEnqueue(() =>
                    {
                        PullStage = p.Status;
                        if (p.Total is long total and > 0 && p.Completed is long done)
                        {
                            PullProgress = Math.Round(done * 100.0 / total, 1);
                            PullStatus = $"{HumanBytes(done)} / {HumanBytes(total)} — {p.Status}";
                        }
                        else
                        {
                            PullStatus = p.Status;
                        }
                    });
                }

                if (!_pullCts.IsCancellationRequested)
                {
                    PullStatus = $"✅ Модель '{name}' готова.";
                    PullProgress = 100;
                    await RefreshAsync();
                }
                else
                {
                    PullStatus = "⏹ Отменено.";
                }
            }
            catch (OperationCanceledException) { PullStatus = "⏹ Отменено."; }
            catch (Exception ex) { PullStatus = $"❌ {ex.Message}"; }
            finally
            {
                IsPulling = false;
                _pullCts?.Dispose();
                _pullCts = null;
            }
        }

        [RelayCommand]
        private void CancelPull()
        {
            _pullCts?.Cancel();
        }

        [RelayCommand]
        private async Task DeleteAsync(ModelItem item)
        {
            if (item is null) return;
            try
            {
                var ok = await _client.DeleteAsync(item.Name);
                if (ok)
                {
                    InstalledModels.Remove(item);
                    StatusText = $"🗑 Удалена: {item.Name}";
                }
                else
                {
                    StatusText = $"❌ Не удалось удалить {item.Name}";
                }
            }
            catch (Exception ex) { StatusText = $"❌ {ex.Message}"; }
        }

        private static string HumanBytes(long b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return $"{v:0.##} {u[i]}";
        }
    }

    public sealed record SuggestedModel(string Name, string Description, string ApproxSize);

    public sealed partial class ModelItem : ObservableObject
    {
        [ObservableProperty] private string name = "";
        [ObservableProperty] private string sizeText = "";
        [ObservableProperty] private string modifiedText = "";
        [ObservableProperty] private string family = "";
        [ObservableProperty] private string quantization = "";
        [ObservableProperty] private string parameterSize = "";

        public static ModelItem From(OllamaLocalModel m) => new()
        {
            Name = m.Name,
            SizeText = Human(m.Size),
            ModifiedText = m.ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            Family = m.Details?.Family ?? "",
            Quantization = m.Details?.QuantizationLevel ?? "",
            ParameterSize = m.Details?.ParameterSize ?? ""
        };

        private static string Human(long b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return $"{v:0.##} {u[i]}";
        }
    }
}
