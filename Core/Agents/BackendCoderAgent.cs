using Core.Configuration;
using Core.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    You are a Backend Coder. Write clean C# (.NET 10):
    services, repositories, SQLite/EF Core, API controllers.
    ALWAYS return code in blocks like:
    ```csharp:Path/To/File.cs
    ... code ...
No explanations outside the blocks.
""";
        public BackendCoderAgent(IAiProviderFactory factory, AgentConfigStore configStore, AgentPromptStore promptStore,
                        ILogger<InterpreterAgent> logger) : base(factory, configStore, promptStore,  logger) { }
        protected override string BuildUserPrompt(AgentContext ctx) => StageContextBuilder.Build(ctx, coderRole: "Кодер-Бэкенд (C#, сервисы, SQLite/EF Core, API)");

      

        protected override Task<AgentResult> PostProcessAsync(AgentContext ctx, string output, CancellationToken ct)
        {
            ctx.SharedData["backend_code"] = output;
            var count = CodeExtractor.Extract(output).Count(b => b.Path is not null);
            if (count == 0)
            {
                var dumpPath = Path.Combine(
                    ctx.OutputRoot ?? PathHelper.GetProjectOutputRoot(ctx.ProjectId),
                    $"_raw_{Role}_{DateTime.Now:HHmmss}.txt");
                try { File.WriteAllText(dumpPath, output); } catch { }
                Logger.LogWarning("[{Role}] Не удалось извлечь блоки. Сырой ответ: {Path}", Role, dumpPath);
            }
            return Task.FromResult(new AgentResult(count > 0, output,
                count == 0 ? "Не удалось извлечь блоки кода." : null));
        }
    }
}
