using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ScrcpyReceive.Media;

internal sealed partial class PlaybackPowerRequest : IDisposable
{
    // REASON_CONTEXT contains a 24-byte union on both supported 64-bit architectures.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct ReasonContext
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public nint SimpleReasonString;
    }

    private enum RequestType { DisplayRequired, SystemRequired }
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle PowerCreateRequest(in ReasonContext reason);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int PowerSetRequest(SafeFileHandle request, RequestType type);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int PowerClearRequest(SafeFileHandle request, RequestType type);
    private SafeFileHandle? handle;

    public PlaybackPowerRequest()
    {
        var reason = Marshal.StringToHGlobalUni("ScrcpyReceive-Windows video playback");
        SafeFileHandle? created = null;
        try
        {
            var context = new ReasonContext { Version = 0, Flags = 1, SimpleReasonString = reason };
            created = PowerCreateRequest(in context);
            if (created.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot create the playback power request.");
            if (PowerSetRequest(created, RequestType.DisplayRequired) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot keep the playback display awake.");
            if (PowerSetRequest(created, RequestType.SystemRequired) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot prevent idle sleep during playback.");
            handle = created;
            created = null;
        }
        finally
        {
            created?.Dispose();
            Marshal.FreeHGlobal(reason);
        }
    }

    public void Dispose()
    {
        var owned = Interlocked.Exchange(ref handle, null);
        if (owned is null) return;
        Exception? failure = null;
        try
        {
            if (PowerClearRequest(owned, RequestType.SystemRequired) == 0)
                failure = new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot clear the idle-sleep playback request.");
            if (PowerClearRequest(owned, RequestType.DisplayRequired) == 0)
            {
                var displayFailure = new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot clear the playback display request.");
                failure = failure is null ? displayFailure : new AggregateException(failure, displayFailure);
            }
        }
        finally { owned.Dispose(); }
        if (failure is not null) throw failure;
    }
}
