using Recents.App.Services.Preview;
using Xunit;

namespace Recents.App.Tests;

public sealed class ExternalPreviewSelectionServiceTests
{
    [Fact]
    public void ResolveNameAgainstDirectoriesReturnsExistingFile()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "recents-preview-selection-" + Guid.NewGuid()));
        var path = Path.Combine(dir.FullName, "demo.pptx");
        File.WriteAllText(path, "placeholder");

        try
        {
            var resolved = ExternalPreviewSelectionService.ResolveNameAgainstDirectories(
                "demo.pptx",
                new[] { dir.FullName });

            Assert.Equal(path, resolved);
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    [Fact]
    public void ExtractPathFragmentsFindsDirectoryInsideUiAutomationText()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "recents-preview-selection-" + Guid.NewGuid()));

        try
        {
            var fragments = ExternalPreviewSelectionService.ExtractPathFragments("Address: " + dir.FullName).ToList();

            Assert.Contains(dir.FullName, fragments);
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }
}
