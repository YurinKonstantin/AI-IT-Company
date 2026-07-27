using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Core.Agents;

public static class CodeExtractor
{
    private static readonly Regex Rx = new(
        @"```(?<lang>[a-zA-Z0-9+#-]+)(?::(?<path>[^\r\n]+))?\r?\n(?<code>.*?)```",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public static IEnumerable<(string Lang, string? Path, string Code)> Extract(string text)
    {
        foreach (Match m in Rx.Matches(text))
            yield return (m.Groups["lang"].Value,
                          m.Groups["path"].Success ? m.Groups["path"].Value.Trim() : null,
                          m.Groups["code"].Value);
    }
}