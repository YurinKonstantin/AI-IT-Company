using System;
using Core.Contracts;

namespace Core.Agents;

/// <summary>
/// Shared WinUI 3 guidance for coder / fixer prompts.
/// Local models often fall back to WPF/UWP; these rules block the common failures.
/// </summary>
internal static class WinUi3PromptRules
{
    /// <summary>Compact block for system prompts (Frontend / Fullstack / ErrorFixer).</summary>
    public const string SystemRules = """
    WINUI 3 STRUCTURE (mandatory — NOT WPF, NOT UWP):
    - Namespaces: Microsoft.UI.Xaml.*, Microsoft.UI.Xaml.Controls — NEVER System.Windows.* or Windows.UI.Xaml.*.
    - Shell: MainWindow is a Window (root). Content pages are Page inside a Frame.
    - Size the Window only: Width/Height/MinWidth/MinHeight on Window (MainWindow.xaml). NEVER set Width/Height on Page.
    - NO Window.DataContext and NO Page.DataContext = ... (that is WPF).
      Correct MVVM wiring on Page:
        public sealed partial class NotesPage : Page {
          public NotesViewModel ViewModel { get; }
          public NotesPage() {
            ViewModel = /* resolve from DI / Host.Services */;
            InitializeComponent();
          }
        }
      XAML: <Page x:DataType="local:NotesPage" ...> and {x:Bind ViewModel.Title, Mode=OneWay}.
    - Navigation: Frame.Navigate(typeof(SomePage)) from MainWindow / NavigationView — NOT Application.MainPage, NOT Frame as Window content replacement via UWP patterns.
    - App.xaml.cs: OnLaunched creates/activates MainWindow. Register services with Microsoft.Extensions.DependencyInjection there.
    - Keep scaffold files (App.xaml, App.xaml.cs, MainWindow.xaml/.cs) — patch them; do not replace with WPF Application / Window patterns.
    - Package references: Microsoft.WindowsAppSDK, CommunityToolkit.Mvvm — not WPF PresentationFramework.

    FORBIDDEN (always wrong in WinUI 3 desktop):
    - Width/Height on <Page>
    - DataContext = new SomeViewModel() or this.DataContext = ...
    - System.Windows.Controls, System.Windows.Window, Application.Current.MainWindow as WPF
    - Windows.UI.Xaml (UWP) APIs as primary UI stack
    - Setting window size from inside a Page code-behind
    """;

    /// <summary>Extra user-prompt rules when the stage targets a WinUI scaffold.</summary>
    public const string UserPromptRules = """
    WINUI 3 (этот проект — Windows App SDK / WinUI 3, НЕ WPF и НЕ UWP):
    - MainWindow.xaml: корневой элемент <Window>, размер окна задавай ТОЛЬКО здесь.
    - Views/*.xaml: корневой элемент <Page> БЕЗ Width/Height; контент растягивай через Grid / * rows.
    - ViewModel: свойство ViewModel на Page + {x:Bind}, x:DataType на корне Page. Без DataContext.
    - DI / bootstrap: правь App.xaml.cs точечно (регистрация сервисов, создание MainWindow в OnLaunched).
    - using: Microsoft.UI.Xaml — никогда System.Windows.
    """;

    public static bool IsWinUiScaffold(string? scaffoldTemplate, ProjectType type)
        => type == ProjectType.WinUI
           || string.Equals(scaffoldTemplate, "WinUI", StringComparison.OrdinalIgnoreCase);
}
