namespace Core.Models;

public enum AttachmentKind { File, Folder }

public sealed class ContextAttachment
{
    public required string Path { get; init; }
    public AttachmentKind Kind { get; init; } = AttachmentKind.File;
    public string DisplayName { get; init; } = "";
}
