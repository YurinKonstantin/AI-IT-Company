using Build;
using Core.Configuration;
using Core.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Agents
{
    /// <summary>
    /// Создаёт каркас проекта по шаблону dotnet new перед тем, как кодеры начнут работу.
    /// LLM не использует.
    /// </summary>
    public sealed class ScaffolderAgent : AgentBase
    {
        public override AgentRole Role => AgentRole.Scaffolder;
        protected override string DefaultSystemPrompt => "(scaffolder, LLM is not used)";

        public ScaffolderAgent(IAiProviderFactory factory, AgentConfigStore configStore, AgentPromptStore promptStore,
                               ILogger<ScaffolderAgent> logger)
            : base(factory, configStore, promptStore, logger) { }

        protected override string BuildUserPrompt(AgentContext ctx) => "";

        public override async Task<AgentResult> ExecuteAsync(AgentContext ctx, CancellationToken ct)
        {
            // Скаффолдим только для новых проектов.
            if (ctx.Mode != WorkMode.CreateNew)
            {
                Logger.LogInformation("[Scaffolder] Пропуск — режим {Mode}.", ctx.Mode);
                return new AgentResult(true, "skipped");
            }

            var template = TemplateRegistry.ForProjectType(ctx.Type.ToString());
            if (template is null)
            {
                Logger.LogWarning("[Scaffolder] Нет шаблона для типа {Type} — сборщик создаст файлы «как есть».", ctx.Type);
                return new AgentResult(true, "no-template");
            }

            // 1) Проверка установленности шаблона
            var installed = await DotnetTemplateService.IsInstalledAsync(template.DotnetNewName, ct);
            if (!installed && !template.IsBuiltin && !string.IsNullOrEmpty(template.InstallPackage))
            {
                Logger.LogInformation("[Scaffolder] Устанавливаю пакет шаблонов: {Pkg}", template.InstallPackage);
                var (instOk, instLog) = await DotnetTemplateService.InstallPackageAsync(template.InstallPackage, ct);
                ctx.SharedData["scaffold_install_log"] = instLog;
                if (!instOk)
                {
                    var msg = $"Не удалось установить шаблон {template.InstallPackage}. " +
                              $"Установите вручную: dotnet new install {template.InstallPackage}";
                    Logger.LogError("[Scaffolder] {Msg}\n{Log}", msg, instLog);
                    return new AgentResult(false, "", msg);
                }
            }
            else if (!installed && template.IsBuiltin)
            {
                Logger.LogWarning("[Scaffolder] Встроенный шаблон {Name} не найден — проверьте установку .NET SDK.",
                                  template.DotnetNewName);
            }

            // 2) Скаффолдинг
            var outputRoot = ctx.OutputRoot
                         ?? ctx.ProjectPath
                         ?? PathHelper.GetProjectOutputRoot(ctx.ProjectId);
            PathHelper.EnsureDirectory(outputRoot);
            ctx.OutputRoot = outputRoot;

            var projectName = SanitizeName(ctx.Plan?.Title ?? "GeneratedApp");
            Logger.LogInformation("[Scaffolder] dotnet new {Tpl} -o {Dir} -n {Name}",
                template.DotnetNewName, outputRoot, projectName);

            var (ok, log) = await DotnetTemplateService.ScaffoldAsync(
                template.DotnetNewName, outputRoot, projectName, ct);

            ctx.SharedData["scaffold_log"] = log;
            ctx.SharedData["scaffold_template"] = template.Key;
            ctx.SharedData["scaffold_name"] = projectName;

            if (!ok)
            {
                Logger.LogError("[Scaffolder] Ошибка скаффолдинга:\n{Log}", log);
                return new AgentResult(false, log, $"dotnet new {template.DotnetNewName} завершился с ошибкой.");
            }

            if (ctx.Type == ProjectType.WindowsService)
                await HardenWindowsServiceAsync(outputRoot, projectName, ctx, ct);

            // 3) Соберём базовый проект, чтобы удостовериться, что каркас рабочий.
            var (bcode, bso, bse) = await DotnetRunner.RunAsync("build", outputRoot, ct);
            ctx.SharedData["scaffold_build_ok"] = (bcode == 0).ToString().ToLower();
            ctx.SharedData["scaffold_build_log"] = bso + "\n" + bse;

            if (bcode != 0)
                Logger.LogWarning("[Scaffolder] Baseline-сборка не прошла. Кодерам будет труднее.");

            // Ключевые файлы каркаса — в SharedData, чтобы кодеры видели реальный WinUI/шаблон,
            // а не подставляли WPF/UWP из обучающих данных.
            var scaffoldFiles = CollectScaffoldSnippets(outputRoot, projectName);
            if (!string.IsNullOrWhiteSpace(scaffoldFiles))
                ctx.SharedData["scaffold_files"] = scaffoldFiles;

            return new AgentResult(true,
                $"Каркас {template.DisplayName} создан в {outputRoot} (проект {projectName}).");
        }

        /// <summary>
        /// Читает точки входа шаблона (App / MainWindow / Program) для передачи кодерам.
        /// </summary>
        private static string CollectScaffoldSnippets(string outputRoot, string projectName)
        {
            var candidates = new[]
            {
                "App.xaml",
                "App.xaml.cs",
                "MainWindow.xaml",
                "MainWindow.xaml.cs",
                "Program.cs",
                Path.Combine(projectName, "App.xaml"),
                Path.Combine(projectName, "App.xaml.cs"),
                Path.Combine(projectName, "MainWindow.xaml"),
                Path.Combine(projectName, "MainWindow.xaml.cs"),
                Path.Combine(projectName, "Program.cs"),
            };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            const int maxPerFile = 2500;

            foreach (var rel in candidates)
            {
                var full = Path.Combine(outputRoot, rel);
                if (!File.Exists(full)) continue;

                var normalized = Path.GetFullPath(full);
                if (!seen.Add(normalized)) continue;

                try
                {
                    var content = File.ReadAllText(full);
                    if (content.Length > maxPerFile)
                        content = content[..maxPerFile] + "\n...(обрезано)";

                    var displayRel = Path.GetRelativePath(outputRoot, full).Replace('\\', '/');
                    var lang = full.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ? "xaml" : "csharp";
                    sb.AppendLine($"```{lang}:{displayRel}");
                    sb.AppendLine(content);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
                catch
                {
                    /* ignore unreadable */
                }
            }

            return sb.ToString().TrimEnd();
        }

        private async Task HardenWindowsServiceAsync(
            string outputRoot, string projectName, AgentContext ctx, CancellationToken ct)
        {
            var csproj = NuGetPackageResolver.FindProjectFiles(outputRoot).FirstOrDefault();
            if (csproj is null) return;

            var (pkgOk, pkgLog) = await DotnetRunner.AddPackageAsync(
                csproj, "Microsoft.Extensions.Hosting.WindowsServices", version: null, ct);
            ctx.SharedData["scaffold_winservice_package"] = pkgOk ? "ok" : "fail";
            ctx.SharedData["scaffold_winservice_package_log"] = pkgLog;
            if (!pkgOk)
                Logger.LogWarning("[Scaffolder] Не удалось добавить WindowsServices:\n{Log}", pkgLog);

            TryPatchProgramForWindowsService(outputRoot, projectName);
        }

        private static void TryPatchProgramForWindowsService(string outputRoot, string projectName)
        {
            var candidates = new[]
            {
                Path.Combine(outputRoot, "Program.cs"),
                Path.Combine(outputRoot, projectName, "Program.cs"),
            };

            foreach (var path in candidates.Where(File.Exists))
            {
                try
                {
                    var text = File.ReadAllText(path);
                    if (text.Contains("AddWindowsService", StringComparison.Ordinal)
                        || text.Contains("UseWindowsService", StringComparison.Ordinal))
                        return;

                    const string marker = "var builder = Host.CreateApplicationBuilder(args);";
                    const string insert = """
                        var builder = Host.CreateApplicationBuilder(args);
                        builder.Services.AddWindowsService(options =>
                        {
                            options.ServiceName = "GeneratedWindowsService";
                        });
                        """;

                    if (!text.Contains(marker, StringComparison.Ordinal))
                        return;

                    text = text.Replace(marker, insert, StringComparison.Ordinal);
                    File.WriteAllText(path, text);
                    return;
                }
                catch
                {
                    /* ignore patch failures */
                }
            }
        }

        private static string SanitizeName(string s)
        {
            var name = new string(s.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            if (name.Length == 0 || char.IsDigit(name[0])) name = "App" + name;
            return name[..Math.Min(name.Length, 40)];
        }
    }
}
