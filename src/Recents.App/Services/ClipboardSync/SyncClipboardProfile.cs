using System.Text.Json;
using System.Text.Json.Serialization;

namespace Recents.App.Services.ClipboardSync;

internal enum SyncClipboardProfileType
{
    Text,
    File,
    Image,
    Group,
    Unknown,
    None
}

internal sealed class SyncClipboardProfile
{
    public const string RemoteProfileFileName = "SyncClipboard.json";
    public const string RemoteFileDirectoryName = "file";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public SyncClipboardProfileType Type { get; set; } = SyncClipboardProfileType.Text;
    public string Hash { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool HasData { get; set; }
    public string? DataName { get; set; }
    public long Size { get; set; }
}
