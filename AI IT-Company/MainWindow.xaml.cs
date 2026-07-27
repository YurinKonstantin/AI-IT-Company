
using AI_IT_Company.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using Views;
using Windows.UI.ApplicationSettings;

namespace AI_IT_Company;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Кастомный titlebar — чтобы NavigationView красиво занял всё окно.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
    }

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        // Открываем стартовую страницу — «Чат».
        NavView.SelectedItem = NavView.MenuItems[0];
        Navigate("chat", new EntranceNavigationTransitionInfo());
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            var transition = args.RecommendedNavigationTransitionInfo
                             ?? new EntranceNavigationTransitionInfo();
            Navigate(tag, transition);
        }
    }

    private void Navigate(string tag, NavigationTransitionInfo transition)
    {
        Type? pageType = tag switch
        {
            "chat" => typeof(ChatPage),
            "dashboard" => typeof(DashboardPage),
            "agents" => typeof(AgentsConfigPage),
            "models" => typeof(ModelsPage),
            "logs" => typeof(LogsPage),
            "settings" => typeof(SettingsPage),
            _ => null
        };

        if (pageType is not null && ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType, null, transition);
    }
}