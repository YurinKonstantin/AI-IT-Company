using AI_IT_Company.Services;
using AI_IT_Company.Views;
using Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.ComponentModel;
using Views;

namespace AI_IT_Company;

public sealed partial class MainWindow : Window
{
    public static MainWindow? CurrentInstance { get; private set; }
    private bool _servicesReady;
    private AppSettingsStore? _settings;
    private PipelineRunService? _runner;
    private bool _isAutopilot;

    public MainWindow()
    {
        InitializeComponent();
        CurrentInstance = this;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        try
        {
            if (MicaController.IsSupported())
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        }
        catch
        {
            // Backdrop optional
        }
    }

    /// <summary>Hide splash overlay and enable navigation after deferred store init.</summary>
    public void MarkServicesReady()
    {
        _servicesReady = true;
        StartupOverlay.Visibility = Visibility.Collapsed;
        NavView.IsEnabled = true;

        _settings = App.Host.Services.GetRequiredService<AppSettingsStore>();
        _runner = App.Host.Services.GetRequiredService<PipelineRunService>();
        _runner.PropertyChanged += Runner_PropertyChanged;
        UpdateReviewBadge();

        ApplyProductModeUi(navigate: true);
    }

    public void SetStartupStatus(string text)
    {
        if (StartupStatusText is not null)
            StartupStatusText.Text = text;
    }

    public void RefreshProductMode(bool navigate = true)
        => ApplyProductModeUi(navigate);

    private async void StudioMode_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        await _settings.SetProductModeAsync("Studio");
        ApplyProductModeUi(navigate: true);
    }

    private async void AutopilotMode_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        await _settings.SetProductModeAsync("Autopilot");
        ApplyProductModeUi(navigate: true);
    }

    private void ApplyProductModeUi(bool navigate)
    {
        _isAutopilot = _settings?.IsAutopilotMode() == true;

        // Visual emphasis on title-bar buttons
        StudioModeBtn.Style = (Style)Application.Current.Resources[
            _isAutopilot ? "DefaultButtonStyle" : "AccentButtonStyle"];
        AutopilotModeBtn.Style = (Style)Application.Current.Resources[
            _isAutopilot ? "AccentButtonStyle" : "DefaultButtonStyle"];

        NavStudioItem.Content = _isAutopilot ? "Studio (Agent)" : "Studio";
        NavAutopilotItem.Content = _isAutopilot ? "Autopilot" : "Autopilot (Freelance)";

        if (!navigate || !_servicesReady) return;

        var tag = _isAutopilot ? "freelance" : "chat";
        NavigateToTag(tag);
    }

    private void Runner_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PipelineRunService.PendingChangesCount)
            or nameof(PipelineRunService.AwaitingChangeReview)
            or nameof(PipelineRunService.AwaitingBuildFixDecision))
        {
            DispatcherQueue.TryEnqueue(UpdateReviewBadge);
        }
    }

    private void UpdateReviewBadge()
    {
        if (_runner is null || ReviewBadge is null) return;
        var n = _runner.PendingChangesCount;
        var waiting = _runner.AwaitingChangeReview || _runner.AwaitingBuildFixDecision;
        if (n > 0 || waiting)
        {
            ReviewBadge.Value = Math.Max(n, waiting ? 1 : 0);
            ReviewBadge.Visibility = Visibility.Visible;
        }
        else
        {
            ReviewBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_servicesReady) return;
        ApplyProductModeUi(navigate: true);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_servicesReady) return;
        if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            var transition = args.RecommendedNavigationTransitionInfo
                             ?? new EntranceNavigationTransitionInfo();
            Navigate(tag, transition);
        }
    }

    public void NavigateToTag(string tag)
    {
        foreach (var obj in NavView.MenuItems)
        {
            if (TrySelectByTag(obj, tag)) return;
        }
        foreach (var obj in NavView.FooterMenuItems)
        {
            if (TrySelectByTag(obj, tag)) return;
        }
        Navigate(tag, new EntranceNavigationTransitionInfo());
    }

    private bool TrySelectByTag(object obj, string tag)
    {
        if (obj is NavigationViewItem item)
        {
            if (item.Tag is string t && t == tag)
            {
                NavView.SelectedItem = item;
                return true;
            }
            foreach (var child in item.MenuItems)
            {
                if (TrySelectByTag(child, tag))
                {
                    item.IsExpanded = true;
                    return true;
                }
            }
        }
        return false;
    }

    private void Navigate(string tag, NavigationTransitionInfo transition)
    {
        Type? pageType = tag switch
        {
            "chat" => typeof(ChatPage),
            "dashboard" => typeof(DashboardPage),
            "changes" => typeof(ChangesPage),
            "freelance" => typeof(FreelancePage),
            "agents" => typeof(AgentsModelsPage),
            "models" => typeof(AgentsModelsPage),
            "logs" => typeof(LogsPage),
            "help" => typeof(HelpPage),
            "settings" => typeof(SettingsPage),
            _ => null
        };

        if (pageType is not null && ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType, null, transition);
    }
}
