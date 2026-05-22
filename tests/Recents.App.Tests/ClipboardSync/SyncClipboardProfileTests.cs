using System.Text.Json;
using Recents.App.Services.ClipboardSync;
using Xunit;

namespace Recents.App.Tests.ClipboardSync;

public sealed class SyncClipboardProfileTests
{
    [Fact]
    public void JsonContract_MatchesSyncClipboardProfileDto()
    {
        var profile = new SyncClipboardProfile
        {
            Type = SyncClipboardProfileType.Text,
            Hash = "HASH",
            Text = "hello",
            HasData = false,
            Size = 5
        };

        var json = JsonSerializer.Serialize(profile, SyncClipboardProfile.JsonOptions);

        Assert.Contains("\"type\":\"Text\"", json);
        Assert.Contains("\"hash\":\"HASH\"", json);
        Assert.Contains("\"text\":\"hello\"", json);
        Assert.Contains("\"hasData\":false", json);
        Assert.Contains("\"size\":5", json);
        Assert.DoesNotContain("schema", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("device", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("updated", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("preview", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plainText", json, StringComparison.OrdinalIgnoreCase);

        var round = JsonSerializer.Deserialize<SyncClipboardProfile>(json, SyncClipboardProfile.JsonOptions)!;
        Assert.Equal(SyncClipboardProfileType.Text, round.Type);
        Assert.Equal("HASH", round.Hash);
        Assert.Equal("hello", round.Text);
    }

    [Fact]
    public void FixedNames_AreStable()
    {
        Assert.Equal("SyncClipboard.json", SyncClipboardProfile.RemoteProfileFileName);
        Assert.Equal("file", SyncClipboardProfile.RemoteFileDirectoryName);
    }
}
