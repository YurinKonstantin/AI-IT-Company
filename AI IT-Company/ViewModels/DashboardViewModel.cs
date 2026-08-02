using AI_IT_Company.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Services;
using Data;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;

namespace AI_IT_Company.ViewModels
{
    public partial class DashboardViewModel : ObservableObject
    {
        public PipelineRunService Runner { get; }
        private readonly SessionStore _sessions;

        public ObservableCollection<SessionListItemVm> Sessions { get; } = new();

        [ObservableProperty] private string sessionsStatus = "";

        public DashboardViewModel(PipelineRunService runner, SessionStore sessions)
        {
            Runner = runner;
            _sessions = sessions;
        }

        [RelayCommand]
        private void Stop() => Runner.Stop();

        [RelayCommand]
        private async Task RetryFailedStageAsync()
        {
            if (!Runner.CanRetryLastFailedStage) return;
            await Runner.RetryLastFailedStageAsync();
        }

        [RelayCommand]
        private void OpenFolder()
        {
            var p = Runner.CurrentOutputRoot;
            if (string.IsNullOrEmpty(p)) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p}\"") { UseShellExecute = true }); }
            catch { }
        }

        [RelayCommand]
        private async Task RefreshSessionsAsync()
        {
            try
            {
                var list = await _sessions.ListRecentAsync(40);
                Sessions.Clear();
                foreach (var s in list)
                    Sessions.Add(SessionListItemVm.From(s));
                SessionsStatus = $"Сессий: {Sessions.Count}";
            }
            catch (System.Exception ex)
            {
                SessionsStatus = "Не удалось загрузить: " + ex.Message;
            }
        }

        [RelayCommand]
        private void OpenSessionFolder(SessionListItemVm? item)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.OutputRoot)) return;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.OutputRoot}\"")
                {
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    public sealed class SessionListItemVm
    {
        public string Id { get; init; } = "";
        public string Mode { get; init; } = "";
        public string Prompt { get; init; } = "";
        public string Status { get; init; } = "";
        public string OutputRoot { get; init; } = "";
        public string StartedAt { get; init; } = "";

        public static SessionListItemVm From(SessionRecord s) => new()
        {
            Id = s.Id,
            Mode = s.Mode,
            Prompt = s.Prompt.Length > 120 ? s.Prompt[..120] + "…" : s.Prompt,
            Status = s.Status,
            OutputRoot = s.OutputRoot,
            StartedAt = s.StartedAt.ToLocalTime().ToString("g")
        };
    }
}
