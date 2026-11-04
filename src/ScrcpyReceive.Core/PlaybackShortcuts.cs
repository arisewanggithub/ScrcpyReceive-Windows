namespace ScrcpyReceive.Core;

public enum FullscreenAction { None, Enter, Exit }

public static class PlaybackShortcuts
{
    public static FullscreenAction Evaluate(ConsoleKey? key, bool hasPresentedVideo, bool fullscreen,
        bool editingText = false, bool modified = false)
    {
        if (key == ConsoleKey.Escape && fullscreen) return FullscreenAction.Exit;
        if (key is not (ConsoleKey.Enter or ConsoleKey.F) || !hasPresentedVideo || editingText || modified)
            return FullscreenAction.None;
        return fullscreen ? FullscreenAction.Exit : FullscreenAction.Enter;
    }
}
