namespace Recents.App.Models;

public sealed class ClipboardFavoriteItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? OriginalItemId { get; set; }
    public ClipboardPayloadType Type { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime SourceCreatedUtc { get; set; } = DateTime.UtcNow;
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
    public int FavoriteOrder { get; set; }
}
