using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Build;

public sealed record BuildError(
    string File,
    int Line,
    int Column,
    string Code,
    string Message,
    string Raw);

/// <summary>Extracts structured MSBuild / compiler errors from a build log.</summary>
public static class BuildErrorParser
{
    // path(line,col): error CS1234: message
    // path(line): error CS1234: message
    private static readonly Regex RxEn = new(
        @"^(?<file>[^\(]+)\((?<line>\d+)(?:,(?<col>\d+))?\)\s*:\s*error\s+(?<code>[A-Z]+\d+)\s*:\s*(?<msg>.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Russian MSBuild fallback: path(line,col): ошибка CS1234: …
    private static readonly Regex RxRu = new(
        @"^(?<file>[^\(]+)\((?<line>\d+)(?:,(?<col>\d+))?\)\s*:\s*ошибка\s+(?<code>[A-Z]+\d+)\s*:\s*(?<msg>.+)$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Generic: … error CS1234: …
    private static readonly Regex RxLoose = new(
        @"^(?<raw>.*\berror\s+(?<code>[A-Z]+\d+)\s*:.*)$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<BuildError> Parse(string log, int max = 50)
    {
        if (string.IsNullOrWhiteSpace(log)) return Array.Empty<BuildError>();

        var list = new List<BuildError>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(Match m, bool hasFile)
        {
            var raw = m.Value.Trim();
            if (!seen.Add(raw)) return;

            var file = hasFile ? m.Groups["file"].Value.Trim() : "";
            int.TryParse(m.Groups["line"].Value, out var line);
            int.TryParse(m.Groups["col"].Value, out var col);
            var code = m.Groups["code"].Value.Trim();
            var msg = hasFile ? m.Groups["msg"].Value.Trim() : raw;

            list.Add(new BuildError(file, line, col, code, msg, raw));
        }

        foreach (Match m in RxEn.Matches(log))
        {
            Add(m, hasFile: true);
            if (list.Count >= max) return list;
        }

        foreach (Match m in RxRu.Matches(log))
        {
            Add(m, hasFile: true);
            if (list.Count >= max) return list;
        }

        if (list.Count == 0)
        {
            foreach (Match m in RxLoose.Matches(log))
            {
                var raw = m.Groups["raw"].Value.Trim();
                if (!seen.Add(raw)) continue;
                list.Add(new BuildError("", 0, 0, m.Groups["code"].Value, raw, raw));
                if (list.Count >= max) break;
            }
        }

        return list;
    }

    /// <summary>Relative paths (normalized) mentioned in errors, for context loading.</summary>
    public static IReadOnlyList<string> DistinctRelativeFiles(
        IEnumerable<BuildError> errors, string? projectRoot, int max = 8)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in errors)
        {
            if (string.IsNullOrWhiteSpace(e.File)) continue;
            var path = e.File.Trim().Trim('"');

            if (!string.IsNullOrEmpty(projectRoot))
            {
                try
                {
                    if (Path.IsPathRooted(path))
                        path = Path.GetRelativePath(projectRoot, path);
                }
                catch { /* keep absolute */ }
            }

            path = path.Replace('\\', '/');
            if (path.StartsWith("..", StringComparison.Ordinal)) continue;
            if (!seen.Add(path)) continue;
            result.Add(path);
            if (result.Count >= max) break;
        }

        return result;
    }

    public static string FormatForPrompt(IReadOnlyList<BuildError> errors, int max = 40)
    {
        if (errors.Count == 0) return "(no structured errors parsed)";
        return string.Join("\n", errors.Take(max).Select(e =>
            string.IsNullOrEmpty(e.File)
                ? e.Raw
                : $"{e.File}({e.Line},{e.Column}): error {e.Code}: {e.Message}"));
    }
}
