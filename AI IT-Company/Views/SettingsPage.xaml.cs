using AI_IT_Company.ViewModels;
using Build;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AI_IT_Company.Views
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsViewModel ViewModel { get; }

        public SettingsPage()
        {
            ViewModel = App.Host.Services.GetRequiredService<SettingsViewModel>();
            InitializeComponent();
        }

        private void OpenRouterKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox box)
                ViewModel.OpenRouterApiKeyInput = box.Password ?? "";
        }

        List<TemplateStatusItem> Items { get; set; } = new();

        public async Task RefreshAsync()
        {
            var installed = await DotnetTemplateService.ListInstalledShortNamesAsync();
            foreach (var it in Items)
            {
                it.IsInstalled = installed.Contains(it.DotnetNewName);
                it.Status = it.IsInstalled ? "✅ установлен" : "❌ отсутствует";
            }
        }

        [RelayCommand]
        public async Task InstallAsync(TemplateStatusItem it)
        {
            if (string.IsNullOrEmpty(it.InstallPackage))
            {
                it.Status = "встроенный (SDK)";
                return;
            }
            it.Status = "устанавливаем…";
            var (ok, log) = await DotnetTemplateService.InstallPackageAsync(it.InstallPackage);
            it.Status = ok ? "✅ установлен" : "❌ ошибка";
            await RefreshAsync();
        }
    }
}
