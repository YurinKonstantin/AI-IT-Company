using AI_IT_Company.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace AI_IT_Company.Views;

public sealed partial class ChangesPage : Page
{
    public ChangesViewModel ViewModel { get; }

    public ChangesPage()
    {
        ViewModel = App.Host.Services.GetRequiredService<ChangesViewModel>();
        InitializeComponent();
        Loaded += (_, _) => ViewModel.AttachUi();
    }
}
