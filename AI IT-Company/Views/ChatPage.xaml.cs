using AI_IT_Company;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ViewModels;
using Windows.System;

namespace Views;

public sealed partial class ChatPage : Page
{
    public ChatViewModel ViewModel { get; }

    public ChatPage()
    {
        ViewModel = App.Host.Services.GetRequiredService<ChatViewModel>();
        InitializeComponent();
        ViewModel.Timeline.CollectionChanged += (_, _) => ScrollTimelineToEnd();
    }

    private void ScrollTimelineToEnd()
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            TimelineScroll.UpdateLayout();
            TimelineScroll.ChangeView(null, TimelineScroll.ScrollableHeight, null);
        });
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        // Ctrl+Enter = send (AcceptsReturn keeps Enter for newlines)
        if (!ctrl) return;
        if (ViewModel.RunCommand.CanExecute(null))
            ViewModel.RunCommand.Execute(null);
        e.Handled = true;
    }
}
