using Recents.App.Services;
using Xunit;

namespace Recents.App.Tests;

public sealed class StartupCommandTests
{
    [Fact]
    public void ParsePreviewCommandAcceptsQuotedPath()
    {
        var command = StartupCommand.Parse(new[] { "--preview", @"C:\Docs\demo.docx" });

        Assert.Equal(StartupCommandKind.PreviewPath, command.Kind);
        Assert.Equal(@"C:\Docs\demo.docx", command.Path);
    }

    [Fact]
    public void ParseDefaultCommandShowsMainWindow()
    {
        var command = StartupCommand.Parse(Array.Empty<string>());

        Assert.Equal(StartupCommandKind.ShowMainWindow, command.Kind);
        Assert.Null(command.Path);
    }

    [Fact]
    public void IpcRoundTripPreservesPreviewPath()
    {
        var message = SingleInstanceCommand.PreviewPath(@"D:\Work\deck.pptx");

        var parsed = SingleInstanceCommand.TryParse(message.Serialize(), out var command);

        Assert.True(parsed);
        Assert.Equal(SingleInstanceCommandKind.PreviewPath, command.Kind);
        Assert.Equal(@"D:\Work\deck.pptx", command.Path);
    }
}
