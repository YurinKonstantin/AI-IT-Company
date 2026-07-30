using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Build
{
    /// <summary>
    /// Простейший применяльщик diff-строк формата «+ &lt;PackageReference ... /&gt;».
    /// Вставляет строки, начинающиеся с "+ " в первый существующий &lt;ItemGroup&gt;,
    /// либо добавляет новую &lt;ItemGroup&gt; в конец Project.
    /// </summary>
    public static class CsprojDiffApplier
    {
        public static async Task ApplyAsync(string csprojPath, string diffText, CancellationToken ct)
        {
            var lines = (await File.ReadAllTextAsync(csprojPath, ct)).Split('\n').ToList();
            var additions = diffText.Split('\n')
                .Where(l => l.TrimStart().StartsWith("+ ") || l.TrimStart().StartsWith("+"))
                .Select(l => l.TrimStart().TrimStart('+').TrimStart())
                .Where(l => l.Length > 0)
                .ToList();
            if (additions.Count == 0) return;

            int insertAt = lines.FindIndex(l => l.Contains("</ItemGroup>"));
            if (insertAt < 0)
            {
                int projectEnd = lines.FindLastIndex(l => l.Contains("</Project>"));
                if (projectEnd < 0) return;
                lines.Insert(projectEnd, "  <ItemGroup>");
                foreach (var a in additions) lines.Insert(projectEnd + 1, "    " + a);
                lines.Insert(projectEnd + 1 + additions.Count, "  </ItemGroup>");
            }
            else
            {
                foreach (var a in additions) lines.Insert(insertAt, "    " + a);
            }

            await File.WriteAllTextAsync(csprojPath, string.Join('\n', lines), ct);
        }
    }
}
