using Build;
using Core.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Core.Agents;

/// <summary>
/// Подбирает релевантные файлы проекта по промпту и deliverables этапа
/// вместо свалки всего репозитория в контекст.
/// </summary>
public static class RelevantContextBuilder
{
    public static string Build(AgentContext ctx, int maxFiles = 12, int maxCharsPerFile = 2200, int maxTotal = 16000)
    {
        var root = ctx.OutputRoot ?? ctx.ProjectPath;
        if (string.IsNullOrWhiteSpace(root) || ctx.Files.Count == 0)
            return "";

        var keywords = ExtractKeywords(ctx);
        if (keywords.Count == 0 && ctx.Mode == WorkMode.CreateNew)
            return ""; // для нового проекта без файлов смысла мало; после scaffold Files заполнены

        var scored = new List<(string Path, int Score)>();
        foreach (var file in ctx.Files)
        {
            if (!IsTextSource(file)) continue;
            var rel = SafeRel(root, file);
            var score = Score(rel, keywords);
            if (score <= 0) continue;
            scored.Add((file, score));
        }

        // Всегда подмешиваем «якорные» файлы с низким приоритетом.
        foreach (var file in ctx.Files.Where(IsAnchorFile).Take(6))
        {
            if (scored.Any(s => s.Path.Equals(file, StringComparison.OrdinalIgnoreCase))) continue;
            scored.Add((file, 1));
        }

        var top = scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
            .Take(maxFiles)
            .ToList();

        if (top.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("=== РЕЛЕВАНТНЫЕ ФАЙЛЫ ПРОЕКТА (выборка по задаче/этапу) ===");
        int total = 0;
        foreach (var (path, score) in top)
        {
            if (total >= maxTotal) break;
            string content;
            try { content = File.ReadAllText(path); }
            catch { continue; }

            if (content.Length > maxCharsPerFile)
                content = content[..maxCharsPerFile] + "\n... (truncated)";

            var rel = SafeRel(root, path).Replace('\\', '/');
            var block = $"--- {rel} (score={score}) ---\n{content}\n\n";
            if (total + block.Length > maxTotal)
            {
                var remain = maxTotal - total;
                if (remain < 200) break;
                block = block[..remain] + "\n...\n";
            }
            sb.Append(block);
            total += block.Length;
        }

        return sb.ToString();
    }

    private static List<string> ExtractKeywords(AgentContext ctx)
    {
        var raw = new StringBuilder();
        raw.Append(ctx.UserPrompt).Append(' ');
        if (ctx.CurrentStage is { } st)
        {
            raw.Append(st.Name).Append(' ').Append(st.Goal).Append(' ');
            foreach (var d in st.Deliverables) raw.Append(d).Append(' ');
            foreach (var a in st.AcceptanceCriteria) raw.Append(a).Append(' ');
            foreach (var s in st.Scope) raw.Append(s).Append(' ');
        }

        var tokens = System.Text.RegularExpressions.Regex
            .Split(raw.ToString().ToLowerInvariant(), @"[^a-zA-Zа-яА-Я0-9_\.]+")
            .Where(t => t.Length >= 3)
            .Where(t => !StopWords.Contains(t))
            .Distinct()
            .Take(40)
            .ToList();

        return tokens;
    }

    private static int Score(string relativePath, List<string> keywords)
    {
        var path = relativePath.Replace('\\', '/').ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        int score = 0;
        foreach (var kw in keywords)
        {
            if (name.Contains(kw, StringComparison.Ordinal)) score += 5;
            else if (path.Contains(kw, StringComparison.Ordinal)) score += 2;
        }

        // Бонусы по расширению/типу
        if (path.EndsWith(".csproj")) score += 1;
        if (path.Contains("/entities/") || path.Contains("/services/") || path.Contains("/viewmodels/"))
            score += 1;

        return score;
    }

    private static bool IsTextSource(string f)
    {
        var ext = Path.GetExtension(f);
        return ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".xaml", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAnchorFile(string f)
    {
        var name = Path.GetFileName(f);
        return name.Equals("Program.cs", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Game1.cs", StringComparison.OrdinalIgnoreCase)
            || name.Equals("App.xaml.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeRel(string root, string full)
    {
        try { return Path.GetRelativePath(root, full); }
        catch { return full; }
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "should", "must",
        "will", "have", "been", "project", "stage", "файл", "нужно", "сделать", "после",
        "using", "class", "public", "private", "void", "async", "task", "string"
    };
}
