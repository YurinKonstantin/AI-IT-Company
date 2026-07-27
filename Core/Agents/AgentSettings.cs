using System;
using System.Collections.Generic;
using System.Text;

namespace Core.Agents
{
    public sealed class AgentSettings
    {
        public string Source { get; set; } = "Ollama";
        public string ModelName { get; set; } = "llama3";
        public int TimeoutSeconds { get; set; } = 0;
        public int MaxTokens { get; set; } = 0;
        public double Temperature { get; set; } = 0.7;
        public int ContextWindow { get; set; } = 128_000;
    }
}
