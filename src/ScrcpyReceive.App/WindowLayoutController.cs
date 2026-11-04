using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using ScrcpyReceive.Core;
using Windows.Graphics;

namespace ScrcpyReceive.App;

internal sealed partial class WindowLayoutController : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint Size; public Rect Bounds, Work; public uint Flags; }
    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);
    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    private static partial int GetMonitorInfo(nint monitor, ref MonitorInfo info);

    private readonly Window window;
    private readonly FrameworkElement root;
    private readonly Action<Exception> report;
    private XamlRoot? xamlRoot;
    private (nint Monitor, uint Dpi, Rect Work)? previous;
    private bool updating;
    private bool disposed;

    public WindowLayoutController(Window window, FrameworkElement root, Action<Exception> report)
    {
        this.window = window;
        this.root = root;
        this.report = report;
        root.Loaded += Loaded;
        window.AppWindow.Changed += Changed;
        Update(true);
    }

    private void Loaded(object sender, RoutedEventArgs args)
    {
        if (xamlRoot is not null) xamlRoot.Changed -= RootChanged;
        xamlRoot = root.XamlRoot;
        xamlRoot.Changed += RootChanged;
        Update(false);
    }

    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Update(false);
    private void Changed(AppWindow sender, AppWindowChangedEventArgs args) => Update(false);

    private void Update(bool initial)
    {
        if (disposed || updating || window.AppWindow.Presenter is not OverlappedPresenter presenter) return;
        updating = true;
        try
        {
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var monitor = MonitorFromWindow(handle, 2);
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (monitor == 0 || GetMonitorInfo(monitor, ref info) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot query display work area.");
            var dpi = GetDpiForWindow(handle);
            var app = window.AppWindow;
            var geometry = (monitor, dpi, info.Work);
            if (!initial && previous.HasValue && previous.Value.Equals(geometry)) return;
            var workWidth = info.Work.Right - info.Work.Left;
            var workHeight = info.Work.Bottom - info.Work.Top;
            var placement = WindowLayout.Place(dpi, info.Work.Left, info.Work.Top, workWidth, workHeight,
                Math.Max(0, app.Size.Width - app.ClientSize.Width), Math.Max(0, app.Size.Height - app.ClientSize.Height));
            presenter.PreferredMinimumWidth = placement.MinimumWidth;
            presenter.PreferredMinimumHeight = placement.MinimumHeight;
            previous = geometry;
            if (initial) app.MoveAndResize(new RectInt32(placement.X, placement.Y, placement.Width, placement.Height));
            else if (presenter.State == OverlappedPresenterState.Restored)
            {
                var width = Math.Clamp(app.Size.Width, placement.MinimumWidth, workWidth);
                var height = Math.Clamp(app.Size.Height, placement.MinimumHeight, workHeight);
                app.MoveAndResize(new RectInt32(Math.Clamp(app.Position.X, info.Work.Left, info.Work.Right - width),
                    Math.Clamp(app.Position.Y, info.Work.Top, info.Work.Bottom - height), width, height));
            }
        }
        catch (Exception error) when (error is Win32Exception or ArgumentException)
        {
            report(error);
        }
        finally { updating = false; }
    }

    public void Dispose()
    {
        disposed = true;
        root.Loaded -= Loaded;
        window.AppWindow.Changed -= Changed;
        if (xamlRoot is not null) xamlRoot.Changed -= RootChanged;
    }
}
