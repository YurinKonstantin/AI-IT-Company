using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Build;

public static class ProjectScanner
{
    public static (string Type, List<string> Files) Scan(string root)
    {
        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml") || f.EndsWith(".csproj"))
            .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\") && !f.Contains("\\Backup\\"))
            .ToList();

        string type = "Unknown";
        var csproj = files.FirstOrDefault(f => f.EndsWith(".csproj"));
        if (csproj != null)
        {
            var txt = File.ReadAllText(csproj);
            if (txt.Contains("MonoGame")) type = "MonogameGame";
            else if (txt.Contains("Microsoft.WindowsAppSDK")) type = "WinUI";
            else if (txt.Contains("Microsoft.AspNetCore")) type = "Api";
            else type = "Console";
        }
        return (type, files);
    }
}