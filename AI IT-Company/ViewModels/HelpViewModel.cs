using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AI_IT_Company.ViewModels;

public partial class HelpViewModel : ObservableObject
{
    private static readonly Regex HeadingRegex = new(@"^##\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    private readonly List<(string Title, string Body)> _sections = new();
    private string _fullMarkdown = "";

    public ObservableCollection<HelpTocItem> Toc { get; } = new();

    [ObservableProperty] private HelpTocItem? selectedToc;
    [ObservableProperty] private string searchQuery = "";
    [ObservableProperty] private string bodyText = "";
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private string footerText = "Версия документа: —";
    [ObservableProperty] private string documentKind = "guide";

    public void Load()
    {
        LoadDocument(DocumentKind == "features" ? "FEATURES.md" : "USER_GUIDE.md");
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    partial void OnSelectedTocChanged(HelpTocItem? value)
    {
        if (value is null) return;
        BodyText = value.Body;
        StatusText = value.Title;
    }

    partial void OnDocumentKindChanged(string value) => Load();

    [RelayCommand]
    private void ShowGuide() => DocumentKind = "guide";

    [RelayCommand]
    private void ShowFeatures() => DocumentKind = "features";

    private void LoadDocument(string fileName)
    {
        _sections.Clear();
        Toc.Clear();

        var path = ResolveDocPath(fileName);
        if (path is null || !File.Exists(path))
        {
            BodyText = $"Не найден файл справки: {fileName}\nОжидается рядом с приложением в папке Docs\\.";
            FooterText = "Версия документа: недоступна";
            StatusText = "Файл не найден";
            return;
        }

        _fullMarkdown = File.ReadAllText(path, Encoding.UTF8);
        ParseSections(_fullMarkdown);
        ParseFooterMeta(_fullMarkdown);
        ApplyFilter();

        if (Toc.Count > 0)
            SelectedToc = Toc[0];
        else
            BodyText = _fullMarkdown;

        StatusText = $"{fileName} · {_sections.Count} разделов";
    }

    private static string? ResolveDocPath(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Docs", fileName),
            Path.Combine(AppContext.BaseDirectory, "docs", fileName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs", fileName)),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "docs", fileName))
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private void ParseSections(string markdown)
    {
        var matches = HeadingRegex.Matches(markdown);
        if (matches.Count == 0)
        {
            _sections.Add(("Документ", markdown.Trim()));
            return;
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var title = matches[i].Groups[1].Value.Trim();
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
            var body = markdown[start..end].Trim();
            _sections.Add((title, body));
        }
    }

    private void ParseFooterMeta(string markdown)
    {
        var version = Regex.Match(markdown, @"Версия документа:\s*([^\n·]+)", RegexOptions.IgnoreCase);
        var updated = Regex.Match(markdown, @"(?:Обновлено|Дата обновления):\s*([^\n]+)", RegexOptions.IgnoreCase);
        var v = version.Success ? version.Groups[1].Value.Trim() : "1.0";
        var d = updated.Success ? updated.Groups[1].Value.Trim() : DateTime.Today.ToString("yyyy-MM-dd");
        FooterText = $"Версия документа: {v} · Обновлено: {d}";
    }

    private void ApplyFilter()
    {
        var q = SearchQuery.Trim();
        Toc.Clear();
        IEnumerable<(string Title, string Body)> src = _sections;
        if (!string.IsNullOrEmpty(q))
            src = _sections.Where(s =>
                s.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.Body.Contains(q, StringComparison.OrdinalIgnoreCase));

        foreach (var s in src)
            Toc.Add(new HelpTocItem(s.Title, s.Body));

        if (SelectedToc is not null)
        {
            var match = Toc.FirstOrDefault(t => t.Title == SelectedToc.Title);
            SelectedToc = match ?? Toc.FirstOrDefault();
        }
        else if (Toc.Count > 0)
        {
            SelectedToc = Toc[0];
        }
        else if (!string.IsNullOrEmpty(q))
        {
            BodyText = "Ничего не найдено по запросу.";
        }
    }
}

public sealed class HelpTocItem
{
    public HelpTocItem(string title, string body)
    {
        Title = title;
        Body = body;
    }

    public string Title { get; }
    public string Body { get; }
}
