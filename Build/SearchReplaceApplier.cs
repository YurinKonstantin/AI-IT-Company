using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Build;

/// <summary>
/// Applies Aider-style SEARCH/REPLACE blocks to an existing file body.
/// </summary>
public static class SearchReplaceApplier
{
    private static readonly Regex RxPair = new(
        @"<<<<<<<\s*SEARCH\s*\r?\n(?<search>.*?)\r?\n=======\s*\r?\n(?<replace>.*?)\r?\n>>>>>>>\s*REPLACE",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool LooksLikeSearchReplace(string text)
        => !string.IsNullOrWhiteSpace(text)
           && text.Contains("<<<<<<<", StringComparison.Ordinal)
           && text.Contains(">>>>>>>", StringComparison.Ordinal);

    public static IReadOnlyList<(string Search, string Replace)> ParsePairs(string text)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        foreach (Match m in RxPair.Matches(text))
        {
            list.Add((
                m.Groups["search"].Value.Replace("\r\n", "\n"),
                m.Groups["replace"].Value.Replace("\r\n", "\n")));
        }

        return list;
    }

    /// <summary>
    /// Applies all pairs in order. Returns (ok, newText, error).
    /// </summary>
    public static (bool Ok, string NewText, string? Error) Apply(string original, string patchText)
    {
        var pairs = ParsePairs(patchText);
        if (pairs.Count == 0)
            return (false, original, "no SEARCH/REPLACE pairs found");

        var text = original.Replace("\r\n", "\n");
        var applied = 0;

        foreach (var (search, replace) in pairs)
        {
            if (string.IsNullOrEmpty(search))
                return (false, original, "empty SEARCH block");

            var idx = text.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0)
            {
                // Try with flexible trailing newline
                var trimmed = search.TrimEnd('\n');
                idx = text.IndexOf(trimmed, StringComparison.Ordinal);
                if (idx < 0)
                    return (false, original,
                        "SEARCH block not found in file (pair " + (applied + 1) + ")");
                text = text[..idx] + replace.TrimEnd('\n') + text[(idx + trimmed.Length)..];
            }
            else
            {
                text = text[..idx] + replace + text[(idx + search.Length)..];
            }

            applied++;
        }

        // Preserve Windows newlines if original used them.
        if (original.Contains("\r\n", StringComparison.Ordinal))
            text = text.Replace("\n", "\r\n");

        return (true, text, null);
    }

    public static string Describe(string patchText)
    {
        var pairs = ParsePairs(patchText);
        var sb = new StringBuilder();
        sb.AppendLine($"(search-replace ×{pairs.Count})");
        foreach (var (s, r) in pairs)
        {
            sb.AppendLine("--- SEARCH ---");
            sb.AppendLine(Truncate(s, 400));
            sb.AppendLine("+++ REPLACE +++");
            sb.AppendLine(Truncate(r, 400));
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}
