using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Core.Agents;

public static class CodeExtractor
{
    private static readonly Regex Rx = new(
       @"```(?<lang>[a-zA-Z0-9+#-]+)(?<diff>:diff)?(?::(?<path>[^\r\n]+))?\r?\n(?<code>.*?)```",
       RegexOptions.Singleline | RegexOptions.Compiled);

    public static IEnumerable<Block> Extract(string text)
    {
        foreach (Match m in Rx.Matches(text))
            yield return new Block(
                Lang: m.Groups["lang"].Value,
                Path: m.Groups["path"].Success ? m.Groups["path"].Value.Trim() : null,
                IsDiff: m.Groups["diff"].Success,
                Code: m.Groups["code"].Value);
    }
    public readonly record struct Block(string Lang, string? Path, bool IsDiff, string Code);
}