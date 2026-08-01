using AI_IT_Company.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace AI_IT_Company.ViewModels
{
    public partial class DashboardViewModel : ObservableObject
    {
        public PipelineRunService Runner { get; }

        public DashboardViewModel(PipelineRunService runner) => Runner = runner;

        [RelayCommand]
        private void Stop() => Runner.Stop();

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
