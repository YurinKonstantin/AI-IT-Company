using Microsoft.UI.Xaml.Controls;
using Views;

namespace AI_IT_Company.Views;

public sealed partial class AgentsModelsPage : Page
{
    private bool _agentsLoaded;
    private bool _modelsLoaded;

    public AgentsModelsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => EnsureTab(0);
    }

    private void RootPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => EnsureTab(RootPivot.SelectedIndex);

    private void EnsureTab(int index)
    {
        if (index == 0 && !_agentsLoaded)
        {
            AgentsFrame.Navigate(typeof(AgentsConfigPage));
            _agentsLoaded = true;
        }
        else if (index == 1 && !_modelsLoaded)
        {
            ModelsFrame.Navigate(typeof(ModelsPage));
            _modelsLoaded = true;
        }
    }
}
