using Core.Agents;
using Core.Configuration;
using Core.Contracts;
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
        Ты — Кодер-Фронтенд для WinUI 3 (.NET 8, Windows App SDK 1.6+).

        ТРЕБОВАНИЯ:
        1. Архитектура — строго MVVM через CommunityToolkit.Mvvm ([ObservableProperty], [RelayCommand]).
        2. XAML: используешь только реальные контролы WinUI 3
           (NavigationView, DataGrid из CommunityToolkit, InfoBar, ContentDialog,
            NumberBox, Expander, ItemsRepeater).
        3. Bindings — {x:Bind} с Mode=OneWay/TwoWay, x:DataType обязателен.
        4. Стили — ThemeResource, никаких хардкод-цветов.
        5. Асинхронность: async Task в командах, DispatcherQueue для UI-потока.
        6. Никаких Windows.Forms / WPF / UWP-специфичных API.

        ФОРМАТ ОТВЕТА (строго):
        - Для каждого файла — отдельный блок:
        ```xaml:Views/SomePage.xaml
        <Page ...>...</Page>
        ```
        ```csharp:Views/SomePage.xaml.cs
        // code-behind
        ```
        ```csharp:ViewModels/SomeViewModel.cs
        // MVVM
        ```
        - Никакого текста вне блоков кода.
    """;

    public FrontendCoderAgent(IAiProviderFactory factory, AgentConfigStore configStore, AgentPromptStore promptStore,
                              ILogger<FrontendCoderAgent> logger)
        : base(factory, configStore, promptStore, logger) { }

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