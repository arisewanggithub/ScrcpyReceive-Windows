using ScrcpyReceive.Core;
using Xunit;

namespace ScrcpyReceive.Tests;

public sealed class ReceiverHomeLayoutTests
{
    [Theory]
    [InlineData(560, 420, 1, 32, false)]
    [InlineData(1040, 820, 1, 46, false)]
    [InlineData(1040, 820, 2, 28, true)]
    [InlineData(560, 420, 2, 22, true)]
    [InlineData(1040, 820, 5, 22, true)]
    [InlineData(1040, 420, 1, 32, false)]
    public void ViewportAndAddressCountSelectReadableLayout(double width, double height, int count, double font, bool multiple)
    {
        var layout = ReceiverHomeLayout.ForViewport(width, height, count);
        Assert.Equal(font, layout.AddressSize);
        Assert.Equal(multiple, layout.MultipleAddresses);
        Assert.InRange(layout.OuterPadding, 12, 32);
    }

    [Fact]
    public void CompactLayoutsReduceChromeWithoutShrinkingAddressesToFit()
    {
        var normal = ReceiverHomeLayout.ForViewport(1040, 820, 1);
        var compact = ReceiverHomeLayout.ForViewport(560, 420, 1);
        Assert.True(compact.OuterPadding < normal.OuterPadding);
        Assert.True(compact.IconSize < normal.IconSize);
        Assert.True(compact.Spacing < normal.Spacing);
        Assert.True(compact.AddressSize >= 32);
        Assert.Equal(22, ReceiverHomeLayout.ForViewport(560, 420, 20).AddressSize);
    }
}
