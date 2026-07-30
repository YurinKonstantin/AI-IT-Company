using System;
using System.Collections.Generic;
using System.Text;

namespace Core.Models
{
    public sealed class ProjectPlan
    {
        public string Title { get; set; } = "";
        public string Summary { get; set; } = "";
        public List<Feature> Features { get; set; } = new();
        public List<Stage> Stages { get; set; } = new();
        public List<string> NonFunctionalRequirements { get; set; } = new();
        public List<string> Constraints { get; set; } = new();
    }

    public sealed class Feature
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Advantage { get; set; } = "";
    }

    public sealed class Stage
    {
        /// <summary>Короткий ID, напр. "S1", "S2". Используется для DependsOn.</summary>
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>Цель этапа — что после него будет работать.</summary>
        public string Goal { get; set; } = "";
        /// <summary>Что должно быть создано/изменено (список кратких пунктов).</summary>
        public List<string> Deliverables { get; set; } = new();
        /// <summary>Критерии приёмки — по чему понимаем, что этап удался.</summary>
        public List<string> AcceptanceCriteria { get; set; } = new();
        /// <summary>ID других этапов, от которых этот зависит.</summary>
        public List<string> DependsOn { get; set; } = new();
        /// <summary>Какие подсистемы задействованы: Backend / Frontend / Game / Tests.</summary>
        public List<string> Scope { get; set; } = new();

        // Runtime-поля (заполняются пайплайном):
        public StageStatus Status { get; set; } = StageStatus.Pending;
        public string? FailReason { get; set; }
        public string? GitCommitSha { get; set; }
        public int Attempts { get; set; }
    }
    public enum StageStatus { Pending, InProgress, Succeeded, Failed, Skipped }
}
