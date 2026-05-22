using System.Text.Json;
using Recents.App.Models;
using Recents.App.Services.ClipboardSync;
using Xunit;

namespace Recents.App.Tests.ClipboardSync;

public sealed class RecentClipboardProfileTests
{
    [Fact]
    public void JsonContract_UsesCamelCaseAndEnumNames()
    {
        var profile = new RecentClipboardProfile
        {
            Schema = RecentClipboardProfile.SchemaVersion,
            App = "Recents",
            DeviceId = "device-1",
            DeviceName = "workstation",
            UpdatedUtc = new DateTime(2026, 5, 23, 1, 2, 3, DateTimeKind.Utc),
            Type = ClipboardPayloadType.Text,
            Hash = "hash",
            PreviewText = "hello",
            PlainText = "hello"
        };

        var json = JsonSerializer.Serialize(profile, RecentClipboardProfile.JsonOptions);

        Assert.Contains("\"schema\":1", json);
        Assert.Contains("\"type\":\"Text\"", json);
        Assert.Contains("\"plainText\":\"hello\"", json);

        var round = JsonSerializer.Deserialize<RecentClipboardProfile>(json, RecentClipboardProfile.JsonOptions)!;
        Assert.Equal(ClipboardPayloadType.Text, round.Type);
        Assert.Equal("hash", round.Hash);
    }

    [Fact]
    public void FixedNames_AreStable()
    {
        Assert.Equal("RecentClipboard.json", RecentClipboardProfile.RemoteProfileFileName);
        Assert.Equal("file", RecentClipboardProfile.RemoteFileDirectoryName);
    }
}
