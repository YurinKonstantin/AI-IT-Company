using Core.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Core.Agents
{
    internal static class StageContextBuilder
    {
        private static string BuildWinUiSection(AgentContext ctx, string scaffoldTemplate)
        {
            if (!WinUi3PromptRules.IsWinUiScaffold(scaffoldTemplate, ctx.Type))
                return "";

            var sb = new StringBuilder();
            sb.AppendLine("=== WINUI 3 ПРАВИЛА ===");
            sb.AppendLine(WinUi3PromptRules.UserPromptRules);

            if (ctx.SharedData.TryGetValue("scaffold_files", out var scaffoldFiles)
                && !string.IsNullOrWhiteSpace(scaffoldFiles))
            {
                sb.AppendLine();
                sb.AppendLine("=== ФАЙЛЫ КАРКАСА (уже на диске — правь поверх, не заменяй на WPF/UWP) ===");
                sb.AppendLine(Truncate(scaffoldFiles, 8000));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Возвращает секцию с кодом, уже написанным предыдущими кодерами на этом этапе.
        /// Для Backend — пусто (он первый). Для Frontend — Backend. Для Game — Backend+Frontend.
        /// Для Tester — всё.
        /// </summary>
        private static string BuildPriorCodeSection(AgentContext ctx, string coderRole)
        {
            // Определяем, какого кодера мы сейчас обслуживаем — по подстроке в роли.
            // Это грубо, но надёжнее, чем прокидывать enum везде.
            var role = coderRole.ToLowerInvariant();

            bool includeBackend = !role.Contains("бэкенд") && !role.Contains("backend");
            bool includeFrontend = !role.Contains("фронтенд") && !role.Contains("frontend")
                                && (role.Contains("game") || role.Contains("гейм")
                                    || role.Contains("тест") || role.Contains("test"));
            bool includeGame = role.Contains("тест") || role.Contains("test");

            var chunks = new List<string>();
            if (includeBackend
                && ctx.SharedData.TryGetValue("backend_code", out var backend)
                && !string.IsNullOrWhiteSpace(backend))
            {
                chunks.Add("--- Код Backend (учитывай контракты, namespace, имена классов и методов) ---\n"
                           + Truncate(backend, 6000));
            }
            if (includeFrontend
                && ctx.SharedData.TryGetValue("frontend_code", out var frontend)
                && !string.IsNullOrWhiteSpace(frontend))
            {
                chunks.Add("--- Код Frontend ---\n" + Truncate(frontend, 4000));
            }
            if (includeGame
                && ctx.SharedData.TryGetValue("game_code", out var game)
                && !string.IsNullOrWhiteSpace(game))
            {
                chunks.Add("--- Код Game ---\n" + Truncate(game, 4000));
            }

            if (chunks.Count == 0) return "";

            return "=== КОД ПРЕДЫДУЩИХ КОДЕРОВ (используй как истину в последней инстанции) ===\n"
                 + string.Join("\n\n", chunks);
        }
        public static string Build(AgentContext ctx, string coderRole, int maxTz = 6000)
        {
            var stage = ctx.CurrentStage;
            var tz = ctx.SharedData.GetValueOrDefault("tz_markdown", "(нет ТЗ)");
            var arch = ctx.Plan is null ? ""
                     : $"Название проекта: {ctx.Plan.Title}\n{ctx.Plan.Summary}";

            var scaffoldTemplate = ctx.SharedData.GetValueOrDefault("scaffold_template", "не определён");
            var scaffoldName = ctx.SharedData.GetValueOrDefault("scaffold_name", "App");
            var scaffoldOk = ctx.SharedData.GetValueOrDefault("scaffold_build_ok", "unknown");

            var stageBlock = stage is null
                ? "(этап не задан — генерируй базовую структуру согласно ТЗ)"
                : $"""
            {stage.Id}. {stage.Name}
            Цель: {stage.Goal}
            Область (scope): {string.Join(", ", stage.Scope)}
            Зависит от: {(stage.DependsOn.Count == 0 ? "—" : string.Join(", ", stage.DependsOn))}

            Что нужно сделать (deliverables):
            {string.Join("\n", stage.Deliverables.Select(d => "- " + d))}

            Критерии приёмки:
            {string.Join("\n", stage.AcceptanceCriteria.Select(a => "- " + a))}
            """;

            // НОВОЕ: собираем код, уже сгенерированный на этом этапе предыдущими кодерами.
            // Frontend увидит Backend, Game увидит и Backend, и Frontend, Tester — всех.
            var priorCode = BuildPriorCodeSection(ctx, coderRole);
            var winUiSection = BuildWinUiSection(ctx, scaffoldTemplate);

            return $"""
        === РОЛЬ ===
        {coderRole}

        === ПРОЕКТ ===
        {arch}

        === ТЕКУЩИЙ ЭТАП ===
        {stageBlock}

        === ТЗ (полное) ===
        {Truncate(tz, maxTz)}

        {priorCode}

        {winUiSection}

        === ПРАВИЛА ===
        1. В корне проекта УЖЕ создан каркас через `dotnet new`.
           - Шаблон: `{scaffoldTemplate}`
           - Имя проекта: `{scaffoldName}`
           - Baseline-сборка каркаса: {scaffoldOk} (true — успех, false — упала, unknown — не собирали).
           Ты работаешь ПОВЕРХ этого каркаса, не заменяя его.

        2. НЕ переписывай `.csproj` целиком. Если нужно добавить NuGet-пакет
           или изменить настройку — используй diff-блок:
           ```xml:diff:{scaffoldName}.csproj
           + <PackageReference Include="EPPlus" Version="7.*" />
           ```
           Каждая новая строка начинается с "+ " и будет вставлена в существующий
           `<ItemGroup>`. Ничего другого в diff-блоке не пиши.

        3. Точки входа шаблона (Program.cs / App.xaml / App.xaml.cs / MainWindow.xaml / Game1.cs
           и др., сгенерированные `dotnet new`) МЕНЯЙ ТОЛЬКО ТОЧЕЧНО:
           - добавляй using и вызовы регистрации,
           - НЕ удаляй существующий bootstrap-код шаблона,
           - при изменении возвращай ПОЛНУЮ актуальную версию файла.

        4. Все новые файлы клади по путям ОТНОСИТЕЛЬНО корня проекта
           (там, где лежит `.csproj`). Пример: `Services/OrderService.cs`,
           `Views/MainPage.xaml`, `Entities/Player.cs`.

        5. Реализуй ТОЛЬКО текущий этап. Ничего сверх deliverables и
           критериев приёмки. Функциональность будущих этапов не трогай.

        6. Не переписывай файлы, созданные предыдущими этапами, если они
           явно не упомянуты в текущем этапе. Итог этапа — минимальный
           diff по отношению к предыдущему успешному коммиту.

        7. СОГЛАСОВАННОСТЬ. Если выше приведён «Код предыдущих кодеров»:
           - используй ИМЕННО ТЕ имена классов, методов, свойств и namespace,
             которые там указаны;
           - НЕ переопределяй сущности, уже созданные другим кодером;
           - если тебе нужен новый метод/поле в чужом классе — вместо переписывания
             попроси его отдельным комментарием в своём коде и работай с тем, что есть.

        8. Возвращай ТОЛЬКО блоки кода с указанием пути.
           Формат:
           ```<lang>:<относительный/путь/к/файлу>
           ...содержимое файла целиком...
           ```
           или для `.csproj`:
           ```xml:diff:<путь>.csproj
           + <строки к добавлению>
           ```
           Никакого текста, пояснений, markdown-заголовков вне блоков кода.

        9. Не выдумывай API, которых не существует. Если сомневаешься —
           используй проверенный, стандартный подход из указанной технологии.
        """;
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "\n…(обрезано)";
    }
}
