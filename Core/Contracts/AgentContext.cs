using Core.Models;
using System;
using System.Collections.Generic;

namespace Core.Contracts
{
    public sealed class AgentContext
    {
        public required string ProjectId { get; init; }
        public required string UserPrompt { get; init; }
        public string? ProjectPath { get; set; }

        /// <summary>Куда фактически пишутся файлы (заполняется BuilderAgent или ViewModel).</summary>
        public string? OutputRoot { get; set; }

        public ProjectType Type { get; set; } = ProjectType.Unknown;
        public WorkMode Mode { get; set; } = WorkMode.CreateNew;

        /// <summary>
        /// Если true — режим выбран в UI и Interpreter не должен его перезаписывать.
        /// </summary>
        public bool ModeLocked { get; set; }

        public Dictionary<string, string> SharedData { get; } = new();
        public List<string> Files { get; } = new();

        /// <summary>Явные вложения пользователя (@file / @folder).</summary>
        public List<ContextAttachment> Attachments { get; } = new();

        public ProjectPlan? Plan { get; set; }
        public Stage? CurrentStage { get; set; }

        /// <summary>Обратный вызов для терминала/pending в ленту пайплайна.</summary>
        public Action<AgentRole, string, string>? RaiseEvent { get; set; }
    }
}
