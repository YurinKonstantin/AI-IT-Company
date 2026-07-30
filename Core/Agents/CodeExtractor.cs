using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Core.Agents;

public static class CodeExtractor
{
    // 1) Строгий (текущий) — приоритет №1
    private static readonly Regex RxStrict = new(
        @"```(?<lang>[a-zA-Z0-9+#\-]+)(?<diff>:diff)?(?:\s*:\s*(?<path>[^\r\n`]+?))?\s*\r?\n(?<code>.*?)```",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // 2) Пробел вместо двоеточия перед путём:  ```csharp Path/To/File.cs
    private static readonly Regex RxSpacePath = new(
        @"```(?<lang>[a-zA-Z0-9+#\-]+)[ \t]+(?<path>[^\s`]+\.[a-zA-Z0-9]+)\s*\r?\n(?<code>.*?)```",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // 3) Путь в комментарии первой строки:  ```csharp\n// File: Path/To/File.cs
    private static readonly Regex RxPathInComment = new(
        @"```(?<lang>[a-zA-Z0-9+#\-]+)\s*\r?\n[ \t]*(?://|#|<!--)\s*(?:File|Filename|Path)\s*[:=]\s*(?<path>[^\r\n]+?)(?:\s*-->)?\s*\r?\n(?<code>.*?)```",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 4) Путь **над** блоком:  **File: X.cs**\n```csharp\n...
    private static readonly Regex RxPathBeforeBlock = new(
        @"(?:\*\*|__)?\s*(?:File|Filename|Файл|Path|Путь)\s*[:=]\s*(?<path>[^\r\n\*]+?)\s*(?:\*\*|__)?\s*\r?\n+```(?<lang>[a-zA-Z0-9+#\-]+)\s*\r?\n(?<code>.*?)```",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 5) Tilde-fences: ~~~csharp:Path.cs ... ~~~
    private static readonly Regex RxTilde = new(
        @"~~~(?<lang>[a-zA-Z0-9+#\-]+)(?<diff>:diff)?(?:\s*:\s*(?<path>[^\r\n]+?))?\s*\r?\n(?<code>.*?)~~~",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public static IEnumerable<Block> Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        // Разворачиваем возможную обёртку ```markdown ... ``` (phi3 любит так делать)
        text = UnwrapOuterFence(text);

        var seen = new HashSet<string>();

        foreach (var rx in new[] { RxStrict, RxSpacePath, RxPathInComment, RxPathBeforeBlock, RxTilde })
        {
            foreach (Match m in rx.Matches(text))
            {
                var path = m.Groups["path"].Success
                    ? NormalizePath(m.Groups["path"].Value)
                    : null;

                var code = m.Groups["code"].Value;

                // Если путь пришёл из комментария — вырезаем этот комментарий из кода,
                // чтобы не оставлять «мусор» в файле.
                if (rx == RxPathInComment && !string.IsNullOrEmpty(code))
                    code = StripFirstLine(code);

                // Ключ для дедупликации, чтобы один и тот же блок не пойман двумя регексами.
                var key = $"{path ?? "-"}::{code.Length}::{code.GetHashCode()}";
                if (!seen.Add(key)) continue;

                yield return new Block(
                    Lang: m.Groups["lang"].Value,
                    Path: path,
                    IsDiff: m.Groups["diff"].Success,
                    Code: code);
            }
        }
    }

    private static string UnwrapOuterFence(string text)
    {
        var t = text.Trim();
        // Ловим случаи вида ```markdown\n ... \n``` или ```md\n...\n```
        var m = Regex.Match(t,
            @"^```(?:markdown|md|text|plain)?\s*\r?\n(?<inner>.*)\r?\n```\s*$",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["inner"].Value : text;
    }

    private static string NormalizePath(string p)
    {
        p = p.Trim().Trim('`', '*', '"', '\'');
        p = p.Replace('\\', '/');
        // Уберём ведущие ./ и /
        while (p.StartsWith("./")) p = p[2..];
        p = p.TrimStart('/');
        return p;
    }

    private static string StripFirstLine(string code)
    {
        var idx = code.IndexOf('\n');
        return idx < 0 ? "" : code[(idx + 1)..];
    }

    public readonly record struct Block(string Lang, string? Path, bool IsDiff, string Code);
}