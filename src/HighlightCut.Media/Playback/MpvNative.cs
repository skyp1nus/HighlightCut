using System.Reflection;
using System.Runtime.InteropServices;

namespace HighlightCut.Media.Playback;

#pragma warning disable CA1707 // The mpv_* names match the C API.

internal enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
}

internal enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    Idle = 11,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
}

internal enum MpvEndFileReason
{
    Eof = 0,
    Stop = 2,
    Quit = 3,
    Error = 4,
    Redirect = 5,
}

internal enum MpvRenderParamType
{
    Invalid = 0,
    ApiType = 1,
    OpenGlInitParams = 2,
    OpenGlFbo = 3,
    FlipY = 4,
    SwSize = 17,
    SwFormat = 18,
    SwStride = 19,
    SwPointer = 20,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserdata;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public IntPtr Name;
    public MpvFormat Format;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public MpvEndFileReason Reason;
    public int Error;
    public long PlaylistEntryId;
    public long PlaylistInsertId;
    public int PlaylistInsertNumEntries;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventLogMessage
{
    public IntPtr Prefix;
    public IntPtr Level;
    public IntPtr Text;
    public int LogLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvRenderParam(MpvRenderParamType type, IntPtr data)
{
    public MpvRenderParamType Type = type;
    public IntPtr Data = data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvOpenGlInitParams
{
    public IntPtr GetProcAddress;
    public IntPtr GetProcAddressContext;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvOpenGlFbo
{
    public int Fbo;
    public int Width;
    public int Height;
    public int InternalFormat;
}

/// <summary>
/// The parts of libmpv's client and render APIs HighlightCut uses (client API 2.x, mpv 0.35 and later).
/// The library is looked up next to the app first (scripts/fetch-deps.ps1 puts libmpv-2.dll there),
/// then in <c>HIGHLIGHTCUT_MPV_DIR</c> and the system library path.
/// </summary>
internal static partial class MpvNative
{
    private const string Library = "mpv";
    public const string MpvDirEnvironmentVariable = "HIGHLIGHTCUT_MPV_DIR";

    static MpvNative() => NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly, Resolve);

    private static string[] Candidates =>
        OperatingSystem.IsWindows() ? ["libmpv-2.dll", "mpv-2.dll", "mpv-1.dll"]
        : OperatingSystem.IsMacOS() ? ["libmpv.2.dylib", "libmpv.dylib"]
        : ["libmpv.so.2", "libmpv.so.1", "libmpv.so"];

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library)
            return IntPtr.Zero;
        var folders = new[] { AppContext.BaseDirectory, Environment.GetEnvironmentVariable(MpvDirEnvironmentVariable) }
            .OfType<string>().Where(d => d.Length > 0).ToList();
        if (OperatingSystem.IsWindows())
            EnsureVulkanLoader(folders);
        foreach (string candidate in Candidates)
        {
            foreach (string folder in folders)
            {
                if (NativeLibrary.TryLoad(Path.Combine(folder, candidate), out IntPtr handle))
                    return handle;
            }
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out IntPtr system))
                return system;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// libmpv-2.dll imports vulkan-1.dll, which normally comes with the GPU driver. Without it (VMs,
    /// the basic display adapter) libmpv would not load at all, so the Khronos loader that
    /// fetch-deps.ps1 puts in vulkan-fallback\ is loaded first; Windows then binds libmpv's import to
    /// the module already in the process. It lives in a subfolder so it never shadows the driver's own.
    /// </summary>
    private static void EnsureVulkanLoader(IEnumerable<string> folders)
    {
        if (NativeLibrary.TryLoad("vulkan-1.dll", out _))
            return;
        foreach (string folder in folders)
        {
            string fallback = Path.Combine(folder, "vulkan-fallback", "vulkan-1.dll");
            if (File.Exists(fallback) && NativeLibrary.TryLoad(fallback, out _))
                return;
        }
    }

    /// <summary>Loads libmpv; returns false with a reason if it is missing or unusable.</summary>
    public static bool TryLoad(out string? error)
    {
        try
        {
            ulong version = mpv_client_api_version();
            if (version >> 16 < 2)
            {
                error = $"libmpv client API {version >> 16}.{version & 0xFFFF} is too old; 2.0 or later is needed.";
                return false;
            }
            error = null;
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            error = "libmpv was not found. Run scripts/fetch-deps.ps1 or install libmpv.";
            return false;
        }
    }

    public static string ErrorText(int error) => Marshal.PtrToStringUTF8(mpv_error_string(error)) ?? $"mpv error {error}";

    [LibraryImport(Library)]
    public static partial ulong mpv_client_api_version();

    [LibraryImport(Library)]
    public static partial IntPtr mpv_error_string(int error);

    [LibraryImport(Library)]
    public static partial IntPtr mpv_create();

    [LibraryImport(Library)]
    public static partial int mpv_initialize(IntPtr ctx);

    [LibraryImport(Library)]
    public static partial void mpv_terminate_destroy(IntPtr ctx);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_option_string(IntPtr ctx, string name, string data);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_get_property(IntPtr ctx, string name, MpvFormat format, out double data);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr mpv_get_property_string(IntPtr ctx, string name);

    [LibraryImport(Library)]
    public static partial void mpv_free(IntPtr data);

    [LibraryImport(Library)]
    public static partial int mpv_command(IntPtr ctx, IntPtr args);

    [LibraryImport(Library)]
    public static partial int mpv_command_async(IntPtr ctx, ulong replyUserdata, IntPtr args);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_observe_property(IntPtr ctx, ulong replyUserdata, string name, MpvFormat format);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_request_log_messages(IntPtr ctx, string minLevel);

    [LibraryImport(Library)]
    public static partial IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [LibraryImport(Library)]
    public static partial void mpv_wakeup(IntPtr ctx);

    [LibraryImport(Library)]
    public static unsafe partial int mpv_render_context_create(out IntPtr res, IntPtr mpv, MpvRenderParam* parameters);

    [LibraryImport(Library)]
    public static unsafe partial void mpv_render_context_set_update_callback(IntPtr ctx,
        delegate* unmanaged[Cdecl]<IntPtr, void> callback, IntPtr callbackContext);

    [LibraryImport(Library)]
    public static partial ulong mpv_render_context_update(IntPtr ctx);

    [LibraryImport(Library)]
    public static unsafe partial int mpv_render_context_render(IntPtr ctx, MpvRenderParam* parameters);

    [LibraryImport(Library)]
    public static partial void mpv_render_context_report_swap(IntPtr ctx);

    [LibraryImport(Library)]
    public static partial void mpv_render_context_free(IntPtr ctx);
}
