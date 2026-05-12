using Recents.App.Services.Preview;
using Xunit;

namespace Recents.App.Tests;

public sealed class ShellPreviewHandlerResolverTests
{
    [Theory]
    [InlineData(720, 1.75, 1260)]
    [InlineData(720.4, 1.25, 901)]
    [InlineData(0, 2.0, 1)]
    public void ToDevicePixelsConvertsWpfDipsToWin32Pixels(double dips, double scale, int expected)
    {
        Assert.Equal(expected, ShellPreviewHost.ToDevicePixels(dips, scale));
    }

    [Fact]
    public void TryResolveUsesExtensionPreviewHandlerRegistration()
    {
        var handler = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var registry = new FakePreviewHandlerRegistry(new Dictionary<string, string?>
        {
            [$@".docx\shellex\{ShellPreviewHandlerResolver.PreviewHandlerShellExtensionGuid:B}"] = handler.ToString("B"),
        });

        var resolved = ShellPreviewHandlerResolver.TryResolve(".docx", registry);

        Assert.Equal(handler, resolved);
    }

    [Fact]
    public void TryResolveFallsBackToProgIdRegistration()
    {
        var handler = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var registry = new FakePreviewHandlerRegistry(new Dictionary<string, string?>
        {
            [".pptx"] = "PowerPoint.Show.12",
            [$@"PowerPoint.Show.12\shellex\{ShellPreviewHandlerResolver.PreviewHandlerShellExtensionGuid:B}"] = handler.ToString("B"),
        });

        var resolved = ShellPreviewHandlerResolver.TryResolve(".pptx", registry);

        Assert.Equal(handler, resolved);
    }

    [Fact]
    public void TryResolveReturnsNullForMissingOrInvalidRegistration()
    {
        var registry = new FakePreviewHandlerRegistry(new Dictionary<string, string?>
        {
            [$@".xlsx\shellex\{ShellPreviewHandlerResolver.PreviewHandlerShellExtensionGuid:B}"] = "not-a-guid",
        });

        var resolved = ShellPreviewHandlerResolver.TryResolve(".xlsx", registry);

        Assert.Null(resolved);
    }

    private sealed class FakePreviewHandlerRegistry : IPreviewHandlerRegistry
    {
        private readonly Dictionary<string, string?> _values;

        public FakePreviewHandlerRegistry(Dictionary<string, string?> values)
        {
            _values = values;
        }

        public string? GetDefaultValue(string subKeyPath) =>
            _values.TryGetValue(subKeyPath, out var value) ? value : null;
    }
}
