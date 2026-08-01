using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Build;

/// <summary>
/// Парсит лог restore/build и определяет отсутствующие NuGet-пакеты.
/// </summary>
public static class NuGetPackageResolver
{
    public sealed record MissingPackage(string PackageId, string? Version, string Reason);

    // NU1101/NU1102: Unable to find package X [version]
    private static readonly Regex RxNuMissing = new(
        @"error\s+NU11(?:01|02)[^\n]*?(?:package|Package)\s+['`]?(?<id>[A-Za-z0-9_.\-]+)['`]?(?:\s*[(\[]\s*(?<ver>[^)\]]+)\s*[)\]])?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RxUnableFindPackage = new(
        @"Unable to find package\s+['`]?(?<id>[A-Za-z0-9_.\-]+)['`]?(?:\s+with version\s+['`]?(?<ver>[^'`\s]+)['`]?)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // CS0246: The type or namespace name 'X' could not be found
    private static readonly Regex RxCs0246 = new(
        @"error\s+CS0246[^\n]*?['`](?<name>[^'`]+)['`]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // NETSDK1004 / missing assets — often fixed by restore, not add package
    private static readonly Regex RxPackageRefNotFound = new(
        @"PackageReference.*?Include\s*=\s*[""'](?<id>[A-Za-z0-9_.\-]+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Известные привязки «пространство имён / тип → NuGet package».
    /// </summary>
    private static readonly Dictionary<string, string> NamespaceToPackage =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.Xna.Framework"] = "MonoGame.Framework.DesktopGL",
            ["Microsoft.Xna"] = "MonoGame.Framework.DesktopGL",
            ["MonoGame.Framework"] = "MonoGame.Framework.DesktopGL",
            ["Microsoft.UI.Xaml"] = "Microsoft.WindowsAppSDK",
            ["Microsoft.UI"] = "Microsoft.WindowsAppSDK",
            ["Microsoft.Windows.ApplicationModel"] = "Microsoft.WindowsAppSDK",
            ["CommunityToolkit.Mvvm"] = "CommunityToolkit.Mvvm",
            ["CommunityToolkit.WinUI"] = "CommunityToolkit.WinUI",
            ["CommunityToolkit.Mvvm.ComponentModel"] = "CommunityToolkit.Mvvm",
            ["CommunityToolkit.Mvvm.Input"] = "CommunityToolkit.Mvvm",
            ["Xunit"] = "xunit",
            ["Xunit.Abstractions"] = "xunit.abstractions",
            ["FluentAssertions"] = "FluentAssertions",
            ["Moq"] = "Moq",
            ["NSubstitute"] = "NSubstitute",
            ["Newtonsoft.Json"] = "Newtonsoft.Json",
            ["Serilog"] = "Serilog",
            ["Serilog.Sinks.File"] = "Serilog.Sinks.File",
            ["Microsoft.Extensions.DependencyInjection"] = "Microsoft.Extensions.DependencyInjection",
            ["Microsoft.Extensions.Hosting"] = "Microsoft.Extensions.Hosting",
            ["Microsoft.Extensions.Logging"] = "Microsoft.Extensions.Logging",
            ["Microsoft.Extensions.Configuration"] = "Microsoft.Extensions.Configuration",
            ["Microsoft.EntityFrameworkCore"] = "Microsoft.EntityFrameworkCore",
            ["Microsoft.EntityFrameworkCore.Sqlite"] = "Microsoft.EntityFrameworkCore.Sqlite",
            ["Microsoft.AspNetCore"] = "Microsoft.AspNetCore.App",
            ["System.Reactive"] = "System.Reactive",
            ["Avalonia"] = "Avalonia",
        };

    public static IReadOnlyList<MissingPackage> ParseMissingPackages(string buildLog)
    {
        if (string.IsNullOrWhiteSpace(buildLog))
            return Array.Empty<MissingPackage>();

        var found = new Dictionary<string, MissingPackage>(StringComparer.OrdinalIgnoreCase);

        void Add(string id, string? ver, string reason)
        {
            id = id.Trim();
            if (string.IsNullOrWhiteSpace(id)) return;
            if (IsIgnoredPackageId(id)) return;

            if (found.TryGetValue(id, out var existing))
            {
                // Предпочитаем запись с версией.
                if (existing.Version is null && ver is not null)
                    found[id] = new MissingPackage(id, CleanVersion(ver), reason);
                return;
            }
            found[id] = new MissingPackage(id, CleanVersion(ver), reason);
        }

        foreach (Match m in RxNuMissing.Matches(buildLog))
            Add(m.Groups["id"].Value, m.Groups["ver"].Success ? m.Groups["ver"].Value : null, "NU110x");

        foreach (Match m in RxUnableFindPackage.Matches(buildLog))
            Add(m.Groups["id"].Value, m.Groups["ver"].Success ? m.Groups["ver"].Value : null, "unable-to-find");

        foreach (Match m in RxPackageRefNotFound.Matches(buildLog))
        {
            // Только если рядом в логе есть nuget/package ошибки.
            if (buildLog.Contains("NU11", StringComparison.OrdinalIgnoreCase)
                || buildLog.Contains("Unable to find package", StringComparison.OrdinalIgnoreCase))
            {
                Add(m.Groups["id"].Value, null, "PackageReference");
            }
        }

        foreach (Match m in RxCs0246.Matches(buildLog))
        {
            var name = m.Groups["name"].Value.Trim();
            if (TryMapTypeOrNamespace(name, out var packageId))
                Add(packageId, null, $"CS0246:{name}");
        }

        return found.Values.ToList();
    }

    public static bool TryMapTypeOrNamespace(string name, out string packageId)
    {
        packageId = "";
        if (string.IsNullOrWhiteSpace(name)) return false;

        // Прямое совпадение или самый длинный префикс namespace.
        if (NamespaceToPackage.TryGetValue(name, out packageId!))
            return true;

        string? bestKey = null;
        foreach (var key in NamespaceToPackage.Keys)
        {
            if (name.StartsWith(key + ".", StringComparison.OrdinalIgnoreCase)
                || name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                if (bestKey is null || key.Length > bestKey.Length)
                    bestKey = key;
            }
        }

        if (bestKey is not null)
        {
            packageId = NamespaceToPackage[bestKey];
            return true;
        }

        // Иногда в CS0246 приходит имя пакета как есть (редкий кейс).
        if (name.Contains('.') && name.Length >= 5 && !name.Contains(' ')
            && char.IsLetter(name[0])
            && NamespaceToPackage.Values.Any(v => v.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            packageId = name;
            return true;
        }

        return false;
    }

    /// <summary>Находит .csproj для dotnet add (предпочтительно в корне).</summary>
    public static IReadOnlyList<string> FindProjectFiles(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Array.Empty<string>();

        static bool IsSkipped(string path)
        {
            var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return parts.Any(p =>
                p.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || p.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || p.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || p.Equals("Backup", StringComparison.OrdinalIgnoreCase));
        }

        var all = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !IsSkipped(f))
            .OrderBy(f => Path.GetRelativePath(root, f).Count(c => c is '/' or '\\'))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return all;
    }

    public static bool CsprojHasPackage(string csprojText, string packageId)
    {
        if (string.IsNullOrWhiteSpace(csprojText) || string.IsNullOrWhiteSpace(packageId))
            return false;

        // Include="Package.Id" или Update="Package.Id"
        var rx = new Regex(
            $@"<(?:PackageReference|PackageVersion)\s+[^>]*(?:Include|Update)\s*=\s*[""']{Regex.Escape(packageId)}[""']",
            RegexOptions.IgnoreCase);
        return rx.IsMatch(csprojText);
    }

    private static bool IsIgnoredPackageId(string id)
    {
        // Framework reference / meta packages that shouldn't be "dotnet add"-ed blindly.
        if (id.Equals("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Equals("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Equals("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            && !id.Equals("System.Reactive", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static string? CleanVersion(string? ver)
    {
        if (string.IsNullOrWhiteSpace(ver)) return null;
        ver = ver.Trim().Trim('(', ')', '[', ']', '\'', '"');
        // Отбрасываем диапазоны вроде [1.0,2.0) — add package лучше без версии.
        if (ver.Contains(',') || ver.Contains('*') || ver.Contains('(') || ver.Contains('['))
            return null;
        return ver;
    }
}
