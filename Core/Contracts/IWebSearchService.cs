using System.Threading;
using System.Threading.Tasks;

namespace Core.Contracts;

/// <summary>
/// Opt-in web search for Architect / ErrorFixer prompts (budgeted snippets).
/// </summary>
public interface IWebSearchService
{
    /// <summary>
    /// Returns a markdown section for the user prompt, or empty if disabled / no key / failure.
    /// </summary>
    Task<string> BuildPromptBlockAsync(string query, CancellationToken ct = default);
}
