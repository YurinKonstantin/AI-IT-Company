using Core.Configuration;
using Core.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace Core.Agents
{
    /// <summary>
    /// Универсальный кодер: одним ответом пишет и backend, и frontend.
    /// Используется, когда пользователь выбрал CoderMode.Unified.
    /// </summary>
    public sealed class FullstackCoderAgent : AgentBase
    {
        public override AgentRole Role => AgentRole.FullstackCoder;

        protected override string DefaultSystemPrompt => """
    You are a Fullstack Coder. In one response, you write BOTH backend AND frontend for a single stage.

    STACK (default):
    - .NET 10, C#, nullable enabled.
    - Backend: services, repositories, SQLite/EF Core, API if needed.
    - Frontend: WinUI 3 (Windows App SDK), MVVM via CommunityToolkit.Mvvm
      ([ObservableProperty], [RelayCommand]). Bindings — {x:Bind}.
    - Styles — ThemeResource, no hardcoded colors and no Windows.Forms/WPF/UWP API.

    """ + WinUi3PromptRules.SystemRules + """

    KEY CONSISTENCY RULE:
    - First, design CONTRACTS (service interfaces, DTOs, namespace).
    - Then implement backend according to these contracts.
    - Then implement frontend that CALLS EXACTLY these contracts.
    - Type/method names MUST match across all files in the response. No
      "OrderService.CreateAsync in one place, OrderService.PlaceOrder in another".
    - DI: services are registered in App.xaml.cs via Microsoft.Extensions.DependencyInjection.

    RESPONSE FORMAT (strict):
    Each file — separate block:
    ```csharp:Services/OrderService.cs
    // ...
    ```
    ```xaml:Views/OrdersPage.xaml
    <Page ...>...</Page>
    ```
    ```csharp:ViewModels/OrdersViewModel.cs
    // MVVM
    ```
    - No text outside blocks.
    - Do not rewrite entire .csproj: for new NuGet packages use diff-block
      ```xml:diff:<ProjectName>.csproj
      + <PackageReference Include="..." Version="..." />
      ```
""";

        public FullstackCoderAgent(IAiProviderFactory factory,
                                   AgentConfigStore configStore,
                                   AgentPromptStore promptStore,
                                   ILogger<FullstackCoderAgent> logger)
            : base(factory, configStore, promptStore, logger) { }

        protected override string BuildUserPrompt(AgentContext ctx)
            => StageContextBuilder.Build(ctx,
                coderRole: "Fullstack-developer (C#/.NET 10, WinUI 3 MVVM + services/SQLite/EF Core)");

        protected override Task<AgentResult> PostProcessAsync(
            AgentContext ctx, string output, CancellationToken ct)
        {
            // Сохраняем в ДВА ключа сразу — это важно:
            // 1) остальные части системы (Tester, ErrorFixer, Builder) ожидают
            //    видеть код в backend_code/frontend_code;
            // 2) их различает Builder при записи файлов на диск — не важно, откуда пришли блоки.
            //ctx.SharedData["backend_code"] = output;
           // ctx.SharedData["frontend_code"] = output;
            // Отдельный ключ на случай, если понадобится узнать, что это был Fullstack:
            ctx.SharedData["fullstack_code"] = output;

            var count = CodeExtractor.Extract(output).Count(b => b.Path is not null);
            Logger.LogInformation("[FullstackCoder] Сгенерировано файлов: {Count}", count);
            Debug.WriteLine("fullstack_code " + output);

            return Task.FromResult(new AgentResult(
                Success: count > 0,
                Output: output,
                Error: count == 0 ? "Не удалось извлечь блоки кода." : null));
        }
    }
}
