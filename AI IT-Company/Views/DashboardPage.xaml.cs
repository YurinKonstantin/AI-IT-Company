using AI_IT_Company.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace AI_IT_Company.Views
{
    public sealed partial class DashboardPage : Page
    {
        public DashboardViewModel ViewModel { get; }

        public DashboardPage()
        {
            ViewModel = App.Host.Services.GetRequiredService<DashboardViewModel>();
            InitializeComponent();
            Loaded += async (_, _) => await ViewModel.RefreshSessionsCommand.ExecuteAsync(null);
        }

        private void Sessions_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SessionListItemVm item)
                ViewModel.OpenSessionFolderCommand.Execute(item);
        }
    }
}
