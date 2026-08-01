using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Build;

/// <summary>
/// Применяет diff-строки формата «+ &lt;PackageReference ... /&gt;» к .csproj.
/// Не дублирует уже существующие PackageReference; предпочитает ItemGroup с пакетами.
/// </summary>
public static class CsprojDiffApplier
{
    private static readonly Regex RxPackageInclude = new(
        @"<(?:PackageReference|PackageVersion)\s+[^>]*(?:Include|Update)\s*=\s*[""'](?<id>[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task ApplyAsync(string csprojPath, string diffText, CancellationToken ct)
    {
        var original = await File.ReadAllTextAsync(csprojPath, ct);
        var nl = original.Contains("\r\n") ? "\r\n" : "\n";
        var lines = original.Replace("\r\n", "\n").Split('\n').ToList();

        var additions = ParseAdditions(diffText);
        if (additions.Count == 0) return;

        // Отфильтровываем уже присутствующие PackageReference.
        var toInsert = new List<string>();
        foreach (var line in additions)
        {
            var id = ExtractPackageId(line);
            if (id is not null && NuGetPackageResolver.CsprojHasPackage(original, id))
                continue;
            // Также не дублируем внутри текущего diff.
            if (id is not null && toInsert.Any(a =>
                    string.Equals(ExtractPackageId(a), id, StringComparison.OrdinalIgnoreCase)))
                continue;
            toInsert.Add(line);
        }

        if (toInsert.Count == 0) return;

        int insertAt = FindPackageItemGroupEnd(lines);
        if (insertAt < 0)
        {
            int projectEnd = lines.FindLastIndex(l =>
                l.Contains("</Project>", StringComparison.OrdinalIgnoreCase));
            if (projectEnd < 0) return;

            var block = new List<string> { "  <ItemGroup>" };
            foreach (var a in toInsert)
                block.Add("    " + a.Trim());
            block.Add("  </ItemGroup>");
            lines.InsertRange(projectEnd, block);
        }
        else
        {
            foreach (var a in toInsert)
                lines.Insert(insertAt++, "    " + a.Trim());
        }

        var result = string.Join(nl, lines);
        // Сохраняем финальный перевод строки, если был.
        if (original.EndsWith("\n") || original.EndsWith("\r\n"))
        {
            if (!result.EndsWith(nl)) result += nl;
        }

        await File.WriteAllTextAsync(csprojPath, result, ct);
    }

    private static List<string> ParseAdditions(string diffText)
    {
        return diffText.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l =>
            {
                var t = l.TrimStart();
                return t.StartsWith("+") && !t.StartsWith("+++");
            })
            .Select(l =>
            {
                var t = l.TrimStart();
                // "+ <PackageReference .../>" или "+<PackageReference"
                t = t[1..].TrimStart();
                return t;
            })
            .Where(l => l.Length > 0)
            .Where(l => l.Contains("PackageReference", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("PackageVersion", StringComparison.OrdinalIgnoreCase)
                     || l.Contains('<')) // прочие ItemGroup-элементы из diff
            .ToList();
    }

    private static string? ExtractPackageId(string line)
    {
        var m = RxPackageInclude.Match(line);
        return m.Success ? m.Groups["id"].Value : null;
    }

    /// <summary>
    /// Индекс строки &lt;/ItemGroup&gt; для группы, где уже есть PackageReference;
    /// иначе — первый &lt;/ItemGroup&gt;.
    /// </summary>
    private static int FindPackageItemGroupEnd(List<string> lines)
    {
        int best = -1;
        int depthStart = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Contains("<ItemGroup", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("</ItemGroup>", StringComparison.OrdinalIgnoreCase))
            {
                depthStart = i;
            }
            else if (line.Contains("</ItemGroup>", StringComparison.OrdinalIgnoreCase) && depthStart >= 0)
            {
                bool hasPkg = false;
                for (int j = depthStart; j <= i; j++)
                {
                    if (lines[j].Contains("PackageReference", StringComparison.OrdinalIgnoreCase)
                        || lines[j].Contains("PackageVersion", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPkg = true;
                        break;
                    }
                }

                if (hasPkg) return i;
                if (best < 0) best = i;
                depthStart = -1;
            }
        }

        return best;
    }
}
