using AI_IT_Company.ViewModels;
using Build;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace AI_IT_Company.Views
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        public SettingsViewModel ViewModel { get; }

        public SettingsPage()
        {
            ViewModel = App.Host.Services.GetRequiredService<SettingsViewModel>();
            InitializeComponent();
        }
        List<TemplateStatusItem> Items { get; set; }
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
    //public sealed partial class TemplateStatusItem : ObservableObject
    //{
    //    public string Key { get; init; } = "";
    //    public string DisplayName { get; init; } = "";
    //    public string DotnetNewName { get; init; } = "";
    //    public string InstallPackage { get; init; } = "";
    //    [ObservableProperty] private bool isInstalled;
    //    [ObservableProperty] private string status = "";
    //}
}
