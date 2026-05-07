namespace Recents.App.Models;

public sealed class ClipboardItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipboardPayloadType Type { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
    public string Hash { get; set; } = string.Empty;
    public string PreviewText { get; set; } = string.Empty;

    public string? PlainText { get; set; }
    public int? TextLength { get; set; }
    public List<ClipboardFilePath> FilePaths { get; set; } = new();

    public string? BlobPath { get; set; }
    public string? HtmlBlobPath { get; set; }
    public string? RtfBlobPath { get; set; }
    public string? ImagePath { get; set; }
    public string? ThumbnailPath { get; set; }

    public int? ImageWidth { get; set; }
    public int? ImageHeight { get; set; }
    public long? SizeBytes { get; set; }

    public string? SourceAppName { get; set; }
    public string? SourceAppPath { get; set; }
    public bool IsFavorite { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedUtc { get; set; }
}

public sealed class ClipboardFilePath
{
    public string Path { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
    public bool ExistsAtCapture { get; set; }
}

public enum ClipboardPayloadType
{
    Text,
    Files,
    Image,
    Html,
    RichText,
    Unknown
}
