using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Build
{
    public sealed record ProjectTemplate(
    string Key,             // "WinUI" | "Api" | "Console" | "MonogameGame" | "ClassLib" | "Xunit"
    string DotnetNewName,   // "winui", "webapi", "console", "mgdesktopgl", "classlib", "xunit"
    string DisplayName,
    string InstallPackage,  // NuGet-пакет с шаблоном (или пусто для встроенных)
    bool IsBuiltin);        // true = входит в .NET SDK; false = нужен install

    public static class TemplateRegistry
    {
        public static IReadOnlyList<ProjectTemplate> All { get; } = new[]
        {
        new ProjectTemplate("Console",      "console",      "Консольное приложение",     "",                                             true),
        new ProjectTemplate("ClassLib",     "classlib",     "Библиотека классов",        "",                                             true),
        new ProjectTemplate("Api",          "webapi",       "ASP.NET Core WebAPI",       "",                                             true),
        new ProjectTemplate("Xunit",        "xunit",        "Проект xUnit-тестов",       "",                                             true),

        // WinUI 3 — идёт из Microsoft.WindowsAppSDK templates (обычно уже установлен вместе с VS/Windows App SDK)
        new ProjectTemplate("WinUI",        "winui",        "WinUI 3 приложение",        "Microsoft.WindowsAppSDK.Templates",             false),

        // MonoGame — нужен пакет шаблонов
        new ProjectTemplate("MonogameGame", "mgdesktopgl",  "MonoGame Desktop GL",       "MonoGame.Templates.CSharp",                     false),
    };

        public static ProjectTemplate? ForProjectType(string projectType) => projectType switch
        {
            "WinUI" => All.First(t => t.Key == "WinUI"),
            "Api" => All.First(t => t.Key == "Api"),
            "Console" => All.First(t => t.Key == "Console"),
            "MonogameGame" => All.First(t => t.Key == "MonogameGame"),
            _ => null
        };
    }
}
