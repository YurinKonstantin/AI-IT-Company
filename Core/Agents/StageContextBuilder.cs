using Core.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Core.Agents
{
    internal static class StageContextBuilder
    {
        /// <summary>Собирает единый блок «текущий этап + ТЗ + правила» для кодеров.</summary>
        public static string Build(AgentContext ctx, string coderRole, int maxTz = 6000)
        {
            var stage = ctx.CurrentStage;
            var tz = ctx.SharedData.GetValueOrDefault("tz_markdown", "(нет ТЗ)");
            var arch = ctx.Plan is null ? "" : $"Название проекта: {ctx.Plan.Title}\n{ctx.Plan.Summary}";

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

            return $"""
            === РОЛЬ ===
            {coderRole}

            === ПРОЕКТ ===
            {arch}

            === ТЕКУЩИЙ ЭТАП ===
            {stageBlock}

            === ТЗ (полное) ===
            {Truncate(tz, maxTz)}

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

            3. Точки входа шаблона (Program.cs / App.xaml / App.xaml.cs / Game1.cs
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

            7. Возвращай ТОЛЬКО блоки кода с указанием пути.
               Формат:
               ```<lang>:<относительный/путь/к/файлу>
               ...содержимое файла целиком...
               ```
               или для `.csproj`:
               ```xml:diff:<путь>.csproj
               + <строки к добавлению>
               ```
               Никакого текста, пояснений, markdown-заголовков вне блоков кода.

            8. Не выдумывай API, которых не существует. Если сомневаешься —
               используй проверенный, стандартный подход из указанной технологии.
            """;
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "\n…(обрезано)";
    }
}
