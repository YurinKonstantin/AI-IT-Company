using Core.Configuration;
using Core.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents
{
    public sealed class BackendCoderAgent : AgentBase
    {
        public override AgentRole Role => AgentRole.BackendCoder;
        protected override string DefaultSystemPrompt => """
        Ты — Кодер-Бэкенд. Пишешь чистый C# (.NET 8+):
        сервисы, репозитории, SQLite/EF Core, API-контроллеры.
        ВСЕГДА возвращай код в блоках вида:
        ```csharp:Path/To/File.cs
        ... код ...
        ```
        Никаких пояснений вне блоков.
    """;
        public BackendCoderAgent(IAiProviderFactory factory, AgentConfigStore configStore, AgentPromptStore promptStore,
                        ILogger<InterpreterAgent> logger) : base(factory, configStore, promptStore,  logger) { }
        protected override string BuildUserPrompt(AgentContext ctx) => StageContextBuilder.Build(ctx, coderRole: "Кодер-Бэкенд (C#, сервисы, SQLite/EF Core, API)");
        protected override Task<AgentResult> PostProcessAsync(AgentContext ctx, string output, CancellationToken ct)
        { 
            ctx.SharedData["backend_code"] = output;
            Debug.WriteLine("backend_code " + output);
            return Task.FromResult(new AgentResult(true, output)); 
        }
    }
}
