using Core.Agents;
using Core.Configuration;
using Core.Contracts;
using Core.Services;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents;

/// <summary>
/// Кодер-фронтенд. Пишет XAML + code-behind для WinUI 3.
/// Следует паттерну MVVM (CommunityToolkit.Mvvm).
/// </summary>
public sealed class FrontendCoderAgent : AgentBase
{
    public override AgentRole Role => AgentRole.FrontendCoder;

    protected override string DefaultSystemPrompt => """
    You are a Frontend Coder for WinUI 3 (.NET 10, Windows App SDK 2.0.1+).

    REQUIREMENTS:
    1. Architecture — strictly MVVM using CommunityToolkit.Mvvm ([ObservableProperty], [RelayCommand]).
    2. XAML: use only real WinUI 3 controls
       (NavigationView, DataGrid from CommunityToolkit, InfoBar, ContentDialog,
        NumberBox, Expander, ItemsRepeater).
    3. Bindings — {x:Bind} with Mode=OneWay/TwoWay, x:DataType is required.
    4. Styles — ThemeResource, no hardcoded colors.
    5. Asynchrony: async Task in commands, DispatcherQueue for UI thread.
    6. No Windows.Forms / WPF / UWP-specific APIs.

    """ + WinUi3PromptRules.SystemRules + """

    TYPICAL LAYOUT:
    - MainWindow.xaml — Window shell + Frame / NavigationView (size here only).
    - Views/FeaturePage.xaml + .xaml.cs — Page with public ViewModel property.
    - ViewModels/FeatureViewModel.cs — [ObservableProperty] / [RelayCommand].

    RESPONSE FORMAT (strict):
    - For each file — separate block:
    ```xaml:Views/SomePage.xaml
    <Page ...>...</Page>
        ```
        ```csharp:Views/SomePage.xaml.cs
        // code-behind
        ```
        ```csharp:ViewModels/SomeViewModel.cs
        // MVVM
        ```
        - No text outside code blocks.
    """;

    public FrontendCoderAgent(IAiProviderFactory factory, AgentConfigStore configStore, AgentPromptStore promptStore,
                              CorrectionLessonStore lessons, AppSettingsStore appSettings,
                              ILogger<FrontendCoderAgent> logger)
        : base(factory, configStore, promptStore, logger, lessons, appSettings) { }

    protected override string BuildUserPrompt(AgentContext ctx) => StageContextBuilder.Build(ctx, coderRole: "Кодер-Фронтенд (WinUI 3, XAML, MVVM/CommunityToolkit.Mvvm)");

    protected override Task<AgentResult> PostProcessAsync(AgentContext ctx, string output, CancellationToken ct)
    {
        ctx.SharedData["frontend_code"] = output;
        var count = CodeExtractor.Extract(output).Count(b => b.Path is not null);
        Logger.LogInformation("[FrontendCoder] Сгенерировано файлов: {Count}", count);
        Debug.WriteLine("frontend_code путь " + output);
        return Task.FromResult(new AgentResult(count > 0, output,
            count == 0 ? "Не удалось извлечь блоки кода." : null));
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "\n...(обрезано)";
}