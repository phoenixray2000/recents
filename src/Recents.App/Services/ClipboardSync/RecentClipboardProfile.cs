using System.Text.Json;
using System.Text.Json.Serialization;
using Recents.App.Models;

namespace Recents.App.Services.ClipboardSync;

internal sealed class RecentClipboardProfile
{
    public const int SchemaVersion = 1;
    public const string RemoteProfileFileName = "RecentClipboard.json";
    public const string RemoteFileDirectoryName = "file";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public int Schema { get; set; } = SchemaVersion;
    public string App { get; set; } = "Recents";
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public ClipboardPayloadType Type { get; set; }
    public string Hash { get; set; } = string.Empty;
    public string PreviewText { get; set; } = string.Empty;
    public string? PlainText { get; set; }
    public string? DataName { get; set; }
    public long? SizeBytes { get; set; }
    public List<RecentClipboardFileEntry> Files { get; set; } = new();
}

internal sealed class RecentClipboardFileEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
}
