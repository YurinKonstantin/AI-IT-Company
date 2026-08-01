using Core.Configuration;
using Core.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents
{
    /// <summary>
    /// Служебный агент-переводчик. Используется TranslationService,
    /// сам по себе в основной pipeline не встраивается.
    /// </summary>
    public sealed class TranslatorAgent : AgentBase
    {
        public override AgentRole Role => AgentRole.Translator;

        protected override string DefaultSystemPrompt => """
        You are a professional translator specialized in technical software
        specifications, source code comments, and product documentation.

        RULES:
        - Translate the entire user message into the requested TARGET language.
        - Preserve the meaning EXACTLY. Do NOT summarize, expand, or explain.
        - Preserve markdown, headings, lists, tables, code blocks, JSON structure,
          file paths, class names, and identifiers UNCHANGED.
        - Never translate code inside fenced code blocks. Only translate comments
          in code and text outside code blocks.
        - Never add any prefix like "Translation:" or wrap the answer in quotes.
        - Output ONLY the translated text. No commentary.
    """;

        public TranslatorAgent(IAiProviderFactory factory,
                               AgentConfigStore configStore,
                               AgentPromptStore promptStore,
                               ILogger<TranslatorAgent> logger)
            : base(factory, configStore, promptStore, logger) { }

        protected override string BuildUserPrompt(AgentContext ctx)
            => ctx.SharedData.GetValueOrDefault("translator_input", "");

        protected override Task<AgentResult> PostProcessAsync(
            AgentContext ctx, string output, CancellationToken ct)
            => Task.FromResult(new AgentResult(true, output));
    }
}
