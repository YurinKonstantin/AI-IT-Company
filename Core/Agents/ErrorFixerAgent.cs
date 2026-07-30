using Core.Agents;
using Core.Configuration;
using Core.Contracts;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents;

/// <summary>
/// Исправитель ошибок. Работает в двух сценариях:
/// 1) После неудачной сборки Builder-агента — читает build_log и генерирует патч.
/// 2) В режиме FixError — анализирует ошибку из промпта пользователя и код проекта.
/// Возвращает ПОЛНЫЕ версии исправленных файлов (не diff-патчи), чтобы Builder перезаписал их.
/// </summary>
public sealed class ErrorFixerAgent : AgentBase
{
    public override AgentRole Role => AgentRole.ErrorFixer;

    protected override string DefaultSystemPrompt => """
        Ты — Исправитель ошибок. Специалист по C# / .NET / WinUI 3 / Monogame.

        АЛГОРИТМ:
        1. Прочитай лог сборки или описание ошибки от пользователя.
        2. Определи КОРЕНЬ проблемы (не симптом): неинициализированное поле,
           неправильный namespace, отсутствующий NuGet, несовместимая версия API и т.д.
        3. Выведи КРАТКИЙ разбор в блоке:
           ```analysis
           Причина: ...
           Файлы к правке: ...
           ```
        4. Затем — ПОЛНЫЕ исправленные версии файлов (не фрагменты!):
           ```csharp:Path/To/File.cs
           // полный файл целиком
           ```
        5. Если нужно добавить NuGet — выведи обновлённый .csproj полностью.
        6. Не придумывай API, которых нет. Если сомневаешься — используй проверенный подход.
        7. Сохраняй существующую логику; меняй минимум, необходимый для исправления.

        ЗАПРЕЩЕНО:
        - Возвращать diff-хунки (@@ ... @@).
        - Возвращать только фрагменты класса.
        - Писать текст вне блоков (кроме блока ```analysis).
    """;

    public ErrorFixerAgent(IAiProviderFactory factory, AgentConfigStore configStore, AgentPromptStore promptStore,
                        ILogger<InterpreterAgent> logger) : base(factory, configStore, promptStore, logger) { }

    protected override string BuildUserPrompt(AgentContext ctx)
    {
        var stage = ctx.CurrentStage;
        var buildLog = ctx.SharedData.GetValueOrDefault("build_log", "");
        var attempt = ctx.SharedData.GetValueOrDefault("fixer_attempt", "0");
        ctx.SharedData["fixer_attempt"] = (int.Parse(attempt) + 1).ToString();

        var stageBlock = stage is null ? "" : $"""
        === ТЕКУЩИЙ ЭТАП ===
        {stage.Id}. {stage.Name}
        Deliverables: {string.Join("; ", stage.Deliverables)}
        """;

        return $"""
        Попытка №{int.Parse(attempt) + 1}. Задача — вернуть код в собираемое состояние в рамках ТЕКУЩЕГО ЭТАПА.
        Не выходи за scope этапа. Если ошибка вне scope — верни только затронутые файлы неизменёнными и укажи это в analysis.

        {stageBlock}

        === Полный лог сборки (последние 6000 симв.) ===
        {TruncateTail(buildLog, 6000)}

        === Код, доступный в этом этапе ===
        {BuildCodeContext(ctx)}

        Верни:
        1) блок ```analysis``` — краткий диагноз;
        2) ПОЛНЫЕ версии файлов, которые надо переписать.
        """;
    }

    protected override Task<AgentResult> PostProcessAsync(AgentContext ctx, string output, CancellationToken ct)
    {
        // Извлекаем аналитический блок.
        var analysisMatch = Regex.Match(output,
            @"```analysis\s*(?<body>.*?)```", RegexOptions.Singleline);
        var analysis = analysisMatch.Success ? analysisMatch.Groups["body"].Value.Trim() : "(анализ не предоставлен)";
        ctx.SharedData["last_fix_analysis"] = analysis;

        // Ключевое: складываем исправления в отдельный слот, откуда Builder их подхватит
        // и перезапишет файлы (accumulating overlay).
        ctx.SharedData["fix_code"] = output;

        var fixedFiles = CodeExtractor.Extract(output)
            .Where(b => b.Path is not null)
            .Select(b => b.Path!)
            .Distinct()
            .ToList();

        ctx.SharedData["last_fixed_files"] = string.Join(";", fixedFiles);

        Logger.LogInformation("[ErrorFixer] Диагноз: {Analysis}", analysis);
        Logger.LogInformation("[ErrorFixer] Исправлено файлов: {Count} → {Files}",
            fixedFiles.Count, string.Join(", ", fixedFiles));

        return Task.FromResult(new AgentResult(
            Success: fixedFiles.Count > 0,
            Output: output,
            Error: fixedFiles.Count == 0 ? "Исправитель не вернул ни одного файла." : null,
            Artifacts: new Dictionary<string, string>
            {
                ["analysis"] = analysis,
                ["fixed_files"] = string.Join(";", fixedFiles)
            }));
    }

    // ---------- helpers ----------

    /// <summary>
    /// Собирает код всех известных файлов (сгенерированных агентами + прочитанных из диска)
    /// в компактный вид для передачи модели.
    /// </summary>
    private static string BuildCodeContext(AgentContext ctx)
    {
        var chunks = new List<string>();

        foreach (var key in new[] { "backend_code", "frontend_code", "game_code", "tests_code", "fix_code" })
        {
            if (ctx.SharedData.TryGetValue(key, out var blob) && !string.IsNullOrWhiteSpace(blob))
                chunks.Add($"# {key}\n{blob}");
        }

        // Если это работа с существующим проектом — подмешиваем реальные файлы с диска.
        if (!string.IsNullOrEmpty(ctx.ProjectPath) && Directory.Exists(ctx.ProjectPath))
        {
            foreach (var file in ctx.Files.Where(IsSourceFile).Take(20))
            {
                try
                {
                    var rel = Path.GetRelativePath(ctx.ProjectPath, file);
                    var content = File.ReadAllText(file);
                    var lang = file.EndsWith(".xaml") ? "xml" : "csharp";
                    chunks.Add($"```{lang}:{rel}\n{Truncate(content, 4000)}\n```");
                }
                catch { /* ignore unreadable */ }
            }
        }

        var joined = string.Join("\n\n", chunks);
        return Truncate(joined, 30_000);
    }

    private static bool IsSourceFile(string f)
        => f.EndsWith(".cs") || f.EndsWith(".xaml") || f.EndsWith(".csproj");

    /// <summary>
    /// Достаёт из лога сборки строки-ошибки формата `... error CS1234: ...`.
    /// </summary>
    private static List<string> ExtractBuildErrors(string log)
    {
        if (string.IsNullOrWhiteSpace(log)) return new();
        var rx = new Regex(@"^.*error\s+[A-Z]+\d+.*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return rx.Matches(log).Select(m => m.Value.Trim()).Distinct().Take(50).ToList();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "\n... (обрезано)";

    private static string TruncateTail(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : "... (начало обрезано)\n" + s[^max..];
}