using Build;
using Core.Configuration;
using Core.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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

            // 3) Соберём базовый проект, чтобы удостовериться, что каркас рабочий.
            var (bcode, bso, bse) = await DotnetRunner.RunAsync("build", outputRoot, ct);
            ctx.SharedData["scaffold_build_ok"] = (bcode == 0).ToString().ToLower();
            ctx.SharedData["scaffold_build_log"] = bso + "\n" + bse;

            if (bcode != 0)
                Logger.LogWarning("[Scaffolder] Baseline-сборка не прошла. Кодерам будет труднее.");

            return new AgentResult(true,
                $"Каркас {template.DisplayName} создан в {outputRoot} (проект {projectName}).");
        }

        private static string SanitizeName(string s)
        {
            var name = new string(s.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            if (name.Length == 0 || char.IsDigit(name[0])) name = "App" + name;
            return name[..Math.Min(name.Length, 40)];
        }
    }
}
