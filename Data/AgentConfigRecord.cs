using System;

namespace Data;

/// <summary>
/// Настройки одного агента, хранятся в SQLite.
/// PK = Role (по одной записи на роль).
/// </summary>
public class AgentConfigRecord
{
    /// <summary>Имя роли агента (AgentRole.ToString()).</summary>
    public string Role { get; set; } = "";

    public string Source { get; set; } = "Ollama";      // "Ollama" | "Microsoft"
    public string ModelName { get; set; } = "llama3";
    public int TimeoutSeconds { get; set; } = 0;        // 0 = ∞
    public int MaxTokens { get; set; } = 0;             // 0 = ∞
    public double Temperature { get; set; } = 0.7;
    public int ContextWindow { get; set; } = 128_000;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}