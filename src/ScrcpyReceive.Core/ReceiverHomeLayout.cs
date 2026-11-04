namespace ScrcpyReceive.Core;

public sealed record ReceiverHomeLayout(double OuterPadding, double Spacing,
    double IconSize, double TitleSize, double AddressSize, bool MultipleAddresses)
{
    public static ReceiverHomeLayout ForViewport(double width, double height, int addressCount)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 || addressCount < 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Viewport dimensions and address count must be valid.");
        var compact = width < 640 || height < 620 || addressCount > 2;
        var multiple = addressCount > 1;
        return new(Math.Clamp(width * 0.035, 12, 32), compact ? 12 : 20,
            compact ? 44 : 72, compact ? 22 : 28,
            multiple ? compact ? 22 : 28 : compact ? 32 : 46, multiple);
    }
}
