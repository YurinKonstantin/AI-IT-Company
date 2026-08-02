using Microsoft.EntityFrameworkCore;
using System;
using System.IO;
using Windows.UI;

namespace Data;

public sealed class AppDbContext : DbContext
{
    public DbSet<AppSettingRecord> AppSettings => Set<AppSettingRecord>();
    public DbSet<AgentConfigRecord> AgentConfigs => Set<AgentConfigRecord>();
    public DbSet<AgentPromptRecord> AgentPrompts => Set<AgentPromptRecord>();
    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<LogRecord> Logs => Set<LogRecord>();
    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();
    public DbSet<FreelanceJobRecord> FreelanceJobs => Set<FreelanceJobRecord>();
    public DbSet<FreelanceOutcomeRecord> FreelanceOutcomes => Set<FreelanceOutcomeRecord>();
    public DbSet<FreelanceSkillStatRecord> FreelanceSkillStats => Set<FreelanceSkillStatRecord>();
    public DbSet<FreelanceAuditRecord> FreelanceAudits => Set<FreelanceAuditRecord>();

    public AppDbContext() { }
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnConfiguring(DbContextOptionsBuilder o)
    {
        if (!o.IsConfigured)
        {
            // Путь берём из PathHelper, чтобы всё лежало в одной корневой папке.
            // Но Data не должна знать про Core — поэтому дублируем логику здесь.
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AiItCompany");
            Directory.CreateDirectory(root);
            var dbPath = Path.Combine(root, "aiitcompany.db");
            o.UseSqlite($"Data Source={dbPath}");
        }
    }
    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<AppSettingRecord>().HasKey(x => x.Key);
        mb.Entity<AgentConfigRecord>().HasKey(x => x.Role);
        mb.Entity<AgentPromptRecord>().HasKey(x => x.Role);
        mb.Entity<ProjectRecord>().HasKey(x => x.Id);
        mb.Entity<LogRecord>().HasKey(x => x.Id);
        mb.Entity<SessionRecord>().HasKey(x => x.Id);
        mb.Entity<FreelanceJobRecord>().HasKey(x => x.Id);
        mb.Entity<FreelanceOutcomeRecord>().HasKey(x => x.Id);
        mb.Entity<FreelanceSkillStatRecord>().HasKey(x => x.Tag);
        mb.Entity<FreelanceAuditRecord>().HasKey(x => x.Id);
    }
}

public class ProjectRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Path { get; set; } = "";
    public string Mode { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? Report { get; set; }
}

public class LogRecord
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string Agent { get; set; } = "";
    public string Level { get; set; } = "Info";
    public string Message { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
}

public class SessionRecord
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string OutputRoot { get; set; } = "";
    public string Status { get; set; } = "";
    public string EventSummary { get; set; } = "";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}

public class FreelanceJobRecord
{
    public string Id { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Source { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Url { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public string? BudgetMin { get; set; }
    public string? BudgetMax { get; set; }
    public string Tags { get; set; } = "";
    public string Status { get; set; } = "New";
    public int FeasibilityScore { get; set; }
    public int FinalScore { get; set; }
    public double ExpectedEffortHours { get; set; }
    public string ExpectedRevenue { get; set; } = "0";
    public string ExpectedProfit { get; set; } = "0";
    public string RiskFlags { get; set; } = "";
    public string Explain { get; set; } = "";
    public string Recommendation { get; set; } = "Review";
    public string? PipelineProjectId { get; set; }
    public string? OutputRoot { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class FreelanceOutcomeRecord
{
    public string Id { get; set; } = "";
    public string JobId { get; set; } = "";
    public bool Success { get; set; }
    public int Rating { get; set; }
    public double? ActualHours { get; set; }
    public string? ActualRevenue { get; set; }
    public string Notes { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class FreelanceSkillStatRecord
{
    public string Tag { get; set; } = "";
    public int Attempts { get; set; }
    public int Successes { get; set; }
    public double AvgRating { get; set; }
    public string AvgProfit { get; set; } = "0";
    /// <summary>EMA-сдвиг feasibility (−20…+20) по итогам оценок.</summary>
    public double FeasibilityBias { get; set; }
}

public class FreelanceAuditRecord
{
    public string Id { get; set; } = "";
    public string Action { get; set; } = "";
    public string JobId { get; set; } = "";
    public string Details { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
}