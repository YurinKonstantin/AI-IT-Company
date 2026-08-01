using AI_IT_Company.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;
using System.Threading.Tasks;

namespace AI_IT_Company.ViewModels
{
    public partial class DashboardViewModel : ObservableObject
    {
        public PipelineRunService Runner { get; }

        public DashboardViewModel(PipelineRunService runner) => Runner = runner;

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
    }
}
