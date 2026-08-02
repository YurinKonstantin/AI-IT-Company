using AI_IT_Company.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace AI_IT_Company.Views;

public sealed partial class FreelancePage : Page
{
    public FreelanceViewModel ViewModel { get; }

    public FreelancePage()
    {
        ViewModel = App.Host.Services.GetRequiredService<FreelanceViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.RefreshCommand.ExecuteAsync(null);
    }
}
