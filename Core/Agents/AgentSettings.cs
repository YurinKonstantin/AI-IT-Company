using System;
using System.Collections.Generic;
using System.Text;

namespace Core.Agents
{
    public sealed class AgentSettings
    {
        public string Source { get; set; } = "Ollama";
        public string ModelName { get; set; } = "qwen2.5-coder:7b";
        public int TimeoutSeconds { get; set; } = 0;
        public int MaxTokens { get; set; } = 0;
        public double Temperature { get; set; } = 0.7;
        /// <summary>Default 8k — 128k num_ctx commonly OOMs local Ollama models.</summary>
        public int ContextWindow { get; set; } = 8_192;
    }
}
