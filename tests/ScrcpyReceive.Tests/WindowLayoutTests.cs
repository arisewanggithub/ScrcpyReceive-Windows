using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class WindowLayoutTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    [InlineData(216)]
    [InlineData(240)]
    [InlineData(288)]
    public void InitialSizeScalesLogicalClientSizeAndFitsWorkArea(int dpi)
    {
        var placement = WindowLayout.Place((uint)dpi, -3200, 80, 3200, 1800, 16, 60);
        Assert.Equal(Math.Min((int)Math.Ceiling(1040 * dpi / 96.0) + 16, 3008), placement.Width);
        Assert.Equal(Math.Min((int)Math.Ceiling(820 * dpi / 96.0) + 60, 1692), placement.Height);
        Assert.InRange(placement.X, -3200, -placement.Width);
        Assert.InRange(placement.Y, 80, 1880 - placement.Height);
        Assert.InRange(placement.MinimumWidth, 1, placement.Width);
        Assert.InRange(placement.MinimumHeight, 1, placement.Height);
    }

    [Fact]
    public void SmallHighDpiDisplayUsesBoundedWindowAndScrollableLayout()
    {
        var placement = WindowLayout.Place(288, 0, 0, 800, 600, 16, 60);
        Assert.Equal(752, placement.Width);
        Assert.Equal(564, placement.Height);
    }

    [Theory]
    [InlineData(0, 800, 600)]
    [InlineData(96, 0, 600)]
    [InlineData(96, 800, 0)]
    public void InvalidDisplayGeometryFailsExplicitly(int dpi, int width, int height) =>
        Assert.Throws<ArgumentException>(() => WindowLayout.Place((uint)dpi, 0, 0, width, height, 0, 0));
}
