using Microsoft.EntityFrameworkCore;
using System;
using System.IO;
using Windows.UI;

namespace Data;

public sealed class AppDbContext : DbContext
{
    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<LogRecord> Logs => Set<LogRecord>();
    public DbSet<AgentConfigRecord> AgentConfigs => Set<AgentConfigRecord>();

    public AppDbContext() { }
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnConfiguring(DbContextOptionsBuilder o)
    {
        if (!o.IsConfigured)
        {
            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AiItCompany", "aiitcompany.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            o.UseSqlite($"Data Source={dbPath}");
        }
    }
    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<AgentConfigRecord>().HasKey(x => x.Role);
        mb.Entity<ProjectRecord>().HasKey(x => x.Id);
        mb.Entity<LogRecord>().HasKey(x => x.Id);
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