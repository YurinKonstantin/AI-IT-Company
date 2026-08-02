using System;

namespace Core.Models;

public enum PendingChangeStatus { Pending, Applied, Rejected }

public sealed class PendingChange
{
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
    public string? OldText { get; init; }
    public required string NewText { get; init; }
    public bool IsCsprojDiff { get; init; }
    public string SourceKey { get; init; } = "";
    public PendingChangeStatus Status { get; set; } = PendingChangeStatus.Pending;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public bool IsSelected { get; set; } = true;

    public string UnifiedDiff { get; set; } = "";
}
