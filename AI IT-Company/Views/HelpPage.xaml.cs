using AI_IT_Company.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AI_IT_Company.Views;

public sealed partial class HelpPage : Page
{
    public HelpViewModel ViewModel { get; }

    public HelpPage()
    {
        ViewModel = App.Host.Services.GetRequiredService<HelpViewModel>();
        InitializeComponent();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e) => ViewModel.Load();
}
