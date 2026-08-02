using System;
using System.Collections.Generic;
using System.Linq;

namespace Build
{
    public sealed record ProjectTemplate(
        string Key,             // "WinUI" | "Api" | "Console" | "MonogameGame" | "Maui" | "WindowsService" | ...
        string DotnetNewName,   // short name for `dotnet new`
        string DisplayName,
        string InstallPackage,  // NuGet template package (empty for builtin)
        bool IsBuiltin);

    public static class TemplateRegistry
    {
        public static IReadOnlyList<ProjectTemplate> All { get; } = new[]
        {
            new ProjectTemplate("Console",        "console",      "Консольное приложение",     "",                                  true),
            new ProjectTemplate("ClassLib",       "classlib",     "Библиотека классов",        "",                                  true),
            new ProjectTemplate("Api",            "webapi",       "ASP.NET Core WebAPI",       "",                                  true),
            new ProjectTemplate("Xunit",          "xunit",        "Проект xUnit-тестов",       "",                                  true),
            new ProjectTemplate("WindowsService", "worker",       "Служба Windows (Worker)",   "",                                  true),

            new ProjectTemplate("WinUI",          "winui",        "WinUI 3 приложение",        "Microsoft.WindowsAppSDK.Templates", false),
            new ProjectTemplate("MonogameGame",   "mgdesktopgl",  "MonoGame Desktop GL",       "MonoGame.Templates.CSharp",         false),
            new ProjectTemplate("Maui",           "maui",         ".NET MAUI",                 "Microsoft.Maui.Templates",          false),
        };

        public static ProjectTemplate? ForProjectType(string projectType) => projectType switch
        {
            "WinUI" => All.First(t => t.Key == "WinUI"),
            "Api" => All.First(t => t.Key == "Api"),
            "Console" => All.First(t => t.Key == "Console"),
            "MonogameGame" => All.First(t => t.Key == "MonogameGame"),
            "Maui" => All.First(t => t.Key == "Maui"),
            "WindowsService" => All.First(t => t.Key == "WindowsService"),
            _ => null
        };
    }
}
