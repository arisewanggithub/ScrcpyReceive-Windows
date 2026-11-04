namespace ScrcpyReceive.Core;

public sealed record WindowPlacement(int X, int Y, int Width, int Height, int MinimumWidth, int MinimumHeight);

public static class WindowLayout
{
    public static WindowPlacement Place(uint dpi, int x, int y, int workWidth, int workHeight,
        int frameWidth, int frameHeight)
    {
        if (dpi == 0 || workWidth <= 0 || workHeight <= 0 || frameWidth < 0 || frameHeight < 0)
            throw new ArgumentException("Invalid display geometry.");
        var maximumWidth = Math.Max(1, (int)(workWidth * 0.94));
        var maximumHeight = Math.Max(1, (int)(workHeight * 0.94));
        int Width(int dips) => (int)Math.Min(maximumWidth, Math.Ceiling(dips * dpi / 96.0) + frameWidth);
        int Height(int dips) => (int)Math.Min(maximumHeight, Math.Ceiling(dips * dpi / 96.0) + frameHeight);
        var width = Width(1040);
        var height = Height(820);
        return new(checked(x + (workWidth - width) / 2), checked(y + (workHeight - height) / 2),
            width, height, Width(560), Height(420));
    }
}
