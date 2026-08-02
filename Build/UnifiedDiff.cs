using System;
using System.Collections.Generic;
using System.Text;

namespace Build;

/// <summary>Простой unified diff без внешних зависимостей (построчно).</summary>
public static class UnifiedDiff
{
    public static string Generate(string? oldText, string? newText, string path, int context = 3)
    {
        oldText ??= "";
        newText ??= "";
        var a = SplitLines(oldText);
        var b = SplitLines(newText);

        var sb = new StringBuilder();
        sb.AppendLine($"--- a/{path.Replace('\\', '/')}");
        sb.AppendLine($"+++ b/{path.Replace('\\', '/')}");

        if (a.Count == 0 && b.Count == 0)
            return sb.ToString();

        // LCS-based Myers-lite: for typical agent files use simple hunk by equal prefix/suffix + middle.
        int prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix]) prefix++;

        int suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix
               && a[a.Count - 1 - suffix] == b[b.Count - 1 - suffix])
            suffix++;

        int aStart = Math.Max(0, prefix - context);
        int bStart = Math.Max(0, prefix - context);
        int aEnd = Math.Min(a.Count, a.Count - suffix + context);
        int bEnd = Math.Min(b.Count, b.Count - suffix + context);

        int oldCount = Math.Max(0, aEnd - aStart);
        int newCount = Math.Max(0, bEnd - bStart);
        sb.AppendLine($"@@ -{aStart + 1},{oldCount} +{bStart + 1},{newCount} @@");

        for (int i = aStart; i < prefix; i++)
            sb.Append(' ').AppendLine(a[i]);

        for (int i = prefix; i < a.Count - suffix; i++)
            sb.Append('-').AppendLine(a[i]);

        for (int i = prefix; i < b.Count - suffix; i++)
            sb.Append('+').AppendLine(b[i]);

        for (int i = a.Count - suffix; i < aEnd && i < a.Count; i++)
            sb.Append(' ').AppendLine(a[i]);

        return sb.ToString();
    }

    private static List<string> SplitLines(string text)
    {
        var list = new List<string>();
        using var reader = new System.IO.StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            list.Add(line);
        return list;
    }
}
