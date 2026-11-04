using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace ScrcpyReceive.Media;

internal static unsafe class NativeRuntime
{
    private static readonly object Gate = new();
    private static bool initialized;

    internal static string RootPath { get { Initialize(); return ffmpeg.RootPath; } }

    public static void Initialize()
    {
        lock (Gate)
        {
            if (initialized) return;
            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64", Architecture.X64 => "x64",
                _ => throw new PlatformNotSupportedException("Native ARM64 or x64 is required.")
            };
            ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "native", $"win-{architecture}");
            if (!File.Exists(Path.Combine(ffmpeg.RootPath, "avcodec-61.dll")))
                throw new NotSupportedException("Pinned decoder runtime is missing. Run tools\\PrepareNative.ps1 and rebuild.");
            if (ffmpeg.avcodec_version() >> 16 != 61 || ffmpeg.avutil_version() >> 16 != 59)
                throw new NotSupportedException("FFmpeg 7.1 ABI is required.");
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
            initialized = true;
        }
    }

    public static void Check(int result, string operation)
    {
        if (result >= 0) return;
        var text = stackalloc byte[256];
        ffmpeg.av_strerror(result, text, 256);
        throw new IOException($"{operation}: {Marshal.PtrToStringUTF8((nint)text)}");
    }
}
