using Recents.App.Helpers;
using Xunit;

namespace Recents.App.Tests;

public sealed class DragSelectionHelperTests
{
    [Fact]
    public void ResolveDragItems_UsesSnapshotWhenCurrentSelectionCollapsesToDragSource()
    {
        var first = new object();
        var second = new object();
        var third = new object();
        var snapshot = DragSelectionHelper.CaptureSnapshot([first, second, third], second);

        var resolved = DragSelectionHelper.ResolveDragItems([second], snapshot);

        Assert.Equal([first, second, third], resolved);
    }

    [Fact]
    public void ResolveDragItems_PrefersCurrentMultiSelectionWhenItIsStillAvailable()
    {
        var first = new object();
        var second = new object();
        var third = new object();
        var fourth = new object();
        var snapshot = DragSelectionHelper.CaptureSnapshot([first, second, third], second);

        var resolved = DragSelectionHelper.ResolveDragItems([second, third, fourth], snapshot);

        Assert.Equal([second, third, fourth], resolved);
    }

    [Fact]
    public void CaptureSnapshot_IgnoresUnselectedDragSource()
    {
        var first = new object();
        var second = new object();
        var third = new object();

        var snapshot = DragSelectionHelper.CaptureSnapshot([first, second], third);

        Assert.Null(snapshot);
    }
}
