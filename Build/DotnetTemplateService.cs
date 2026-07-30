using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Build
{
    public static class DotnetTemplateService
    {
        /// <summary>
        /// Возвращает список установленных шаблонов (dotnet new list --output-format table).
        /// Ищем по «Short Name».
        /// </summary>
        public static async Task<HashSet<string>> ListInstalledShortNamesAsync(CancellationToken ct = default)
        {
            var (code, so, _) = await DotnetRunner.RunAsync("new list", Environment.CurrentDirectory, ct);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (code != 0) return set;

            // Формат таблицы: "Template Name  Short Name  Language  Tags"
            // Парсим строки с несколькими пробелами.
            var rx = new Regex(@"^\s*.+?\s{2,}(?<short>\S+)\s{2,}", RegexOptions.Compiled);
            foreach (var raw in so.Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0) continue;
                var m = rx.Match(line);
                if (m.Success)
                {
                    // short может быть списком через запятую
                    foreach (var part in m.Groups["short"].Value.Split(','))
                        set.Add(part.Trim());
                }
            }
            return set;
        }

        public static async Task<bool> IsInstalledAsync(string dotnetNewName, CancellationToken ct = default)
        {
            var installed = await ListInstalledShortNamesAsync(ct);
            return installed.Contains(dotnetNewName);
        }

        /// <summary>Установить пакет шаблонов (dotnet new install &lt;package&gt;).</summary>
        public static async Task<(bool Ok, string Log)> InstallPackageAsync(string package, CancellationToken ct = default)
        {
            var (code, so, se) = await DotnetRunner.RunAsync(
                $"new install {package}",
                Environment.CurrentDirectory,
                ct);
            return (code == 0, so + "\n" + se);
        }

        /// <summary>
        /// Создать проект: dotnet new &lt;template&gt; -o &lt;dir&gt; -n &lt;name&gt; --force
        /// </summary>
        public static async Task<(bool Ok, string Log)> ScaffoldAsync(
            string dotnetNewName,
            string outputDir,
            string projectName,
            CancellationToken ct = default)
        {
            Directory.CreateDirectory(outputDir);
            var args = $"new {dotnetNewName} -o \"{outputDir}\" -n \"{projectName}\" --force";
            var (code, so, se) = await DotnetRunner.RunAsync(args, Environment.CurrentDirectory, ct);
            return (code == 0, so + "\n" + se);
        }
    }
}
