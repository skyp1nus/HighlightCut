using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HighlightCut.Media.Playback;

/// <summary>
/// An mpv render context: mpv decodes, the app decides when and where frames are drawn.
/// </summary>
/// <remarks>
/// mpv's render rules: call <see cref="HasNewFrame"/>, the Render methods and <see cref="Dispose"/>
/// from one thread at a time, never from inside <see cref="UpdateRequested"/>, and do not make
/// synchronous libmpv calls on that thread (<see cref="MpvPlayer"/>'s controls are asynchronous).
/// </remarks>
public abstract class MpvRenderer : IDisposable
{
    private static readonly IntPtr ApiSoftware = Marshal.StringToCoTaskMemUTF8("sw");
    private static readonly IntPtr ApiOpenGl = Marshal.StringToCoTaskMemUTF8("opengl");

    private readonly MpvPlayer _player;
    private GCHandle _self;
    private IntPtr _context;

    private protected MpvRenderer(MpvPlayer player) => _player = player;

    /// <summary>
    /// Raised on an mpv thread when there may be a new frame. Render soon, on your render thread;
    /// do not call into mpv from the handler.
    /// </summary>
    public event EventHandler? UpdateRequested;

    protected IntPtr Context => _context;

    private protected unsafe void Create(bool openGl, MpvOpenGlInitParams* glParams)
    {
        _self = GCHandle.Alloc(this);
        var parameters = stackalloc MpvRenderParam[3];
        parameters[0] = new MpvRenderParam(MpvRenderParamType.ApiType, openGl ? ApiOpenGl : ApiSoftware);
        parameters[1] = openGl ? new MpvRenderParam(MpvRenderParamType.OpenGlInitParams, (IntPtr)glParams) : default;
        parameters[2] = default;
        int status = MpvNative.mpv_render_context_create(out _context, _player.Handle, parameters);
        if (status < 0)
        {
            _self.Free();
            throw new MpvException("mpv could not create a renderer: " + MpvNative.ErrorText(status));
        }
        MpvNative.mpv_render_context_set_update_callback(_context, &OnUpdate, GCHandle.ToIntPtr(_self));
        _player.Attach(this);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUpdate(IntPtr context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is MpvRenderer renderer)
                renderer.UpdateRequested?.Invoke(renderer, EventArgs.Empty);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine("mpv update callback failed: " + e);
        }
    }

    /// <summary>Whether mpv has a new frame to draw (also lets mpv advance its render state).</summary>
    public bool HasNewFrame() => _context != IntPtr.Zero && (MpvNative.mpv_render_context_update(_context) & 1) != 0;

    protected static void Check(int status)
    {
        if (status < 0)
            throw new MpvException("mpv could not render: " + MpvNative.ErrorText(status));
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual unsafe void Dispose(bool disposing)
    {
        if (_context == IntPtr.Zero)
            return;
        // First, while the file and position are still there: freeing the context takes the video output away, and
        // mpv may end the file with an error.
        _player.Detach(this);
        MpvNative.mpv_render_context_set_update_callback(_context, null, IntPtr.Zero);
        MpvNative.mpv_render_context_free(_context);
        _context = IntPtr.Zero;
        if (_self.IsAllocated)
            _self.Free();
    }
}

/// <summary>Renders video into memory (32-bit BGRX), scaled and letterboxed to the requested size.</summary>
public sealed class MpvSoftwareRenderer : MpvRenderer
{
    private static readonly IntPtr Format = Marshal.StringToCoTaskMemUTF8("bgr0");

    public unsafe MpvSoftwareRenderer(MpvPlayer player)
        : base(player) => Create(openGl: false, null);

    /// <summary>
    /// Draws the current frame into <paramref name="pixels"/>. Pointer and stride should be
    /// 64-byte aligned; the fourth byte of each pixel is undefined.
    /// </summary>
    public unsafe void Render(IntPtr pixels, int width, int height, int stride)
    {
        if (Context == IntPtr.Zero || width <= 0 || height <= 0)
            return;
        int* size = stackalloc int[2];
        size[0] = width;
        size[1] = height;
        nuint strideValue = (nuint)stride;
        var parameters = stackalloc MpvRenderParam[5];
        parameters[0] = new MpvRenderParam(MpvRenderParamType.SwSize, (IntPtr)size);
        parameters[1] = new MpvRenderParam(MpvRenderParamType.SwFormat, Format);
        parameters[2] = new MpvRenderParam(MpvRenderParamType.SwStride, (IntPtr)(&strideValue));
        parameters[3] = new MpvRenderParam(MpvRenderParamType.SwPointer, pixels);
        parameters[4] = default;
        Check(MpvNative.mpv_render_context_render(Context, parameters));
    }
}

/// <summary>Renders video with OpenGL into a framebuffer. Every call needs the GL context current.</summary>
public sealed class MpvOpenGlRenderer : MpvRenderer
{
    private readonly Func<string, IntPtr> _getProcAddress;

    /// <param name="getProcAddress">Looks up OpenGL functions in the context mpv should use.</param>
    public unsafe MpvOpenGlRenderer(MpvPlayer player, Func<string, IntPtr> getProcAddress)
        : base(player)
    {
        _getProcAddress = getProcAddress;
        var self = GCHandle.Alloc(this, GCHandleType.Normal);
        try
        {
            var init = new MpvOpenGlInitParams
            {
                GetProcAddress = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&GetProcAddress,
                GetProcAddressContext = GCHandle.ToIntPtr(self),
            };
            Create(openGl: true, &init);
        }
        finally
        {
            // mpv only looks functions up while creating the context.
            self.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IntPtr GetProcAddress(IntPtr context, IntPtr name)
    {
        try
        {
            return GCHandle.FromIntPtr(context).Target is MpvOpenGlRenderer renderer && Marshal.PtrToStringUTF8(name) is { } n
                ? renderer._getProcAddress(n)
                : IntPtr.Zero;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Draws the current frame into framebuffer <paramref name="framebuffer"/> (0 is the default one).</summary>
    public unsafe void Render(int framebuffer, int width, int height, bool flipY)
    {
        if (Context == IntPtr.Zero || width <= 0 || height <= 0)
            return;
        var fbo = new MpvOpenGlFbo { Fbo = framebuffer, Width = width, Height = height };
        int flip = flipY ? 1 : 0;
        var parameters = stackalloc MpvRenderParam[3];
        parameters[0] = new MpvRenderParam(MpvRenderParamType.OpenGlFbo, (IntPtr)(&fbo));
        parameters[1] = new MpvRenderParam(MpvRenderParamType.FlipY, (IntPtr)(&flip));
        parameters[2] = default;
        Check(MpvNative.mpv_render_context_render(Context, parameters));
    }

    /// <summary>Tells mpv the frame was presented (improves frame timing).</summary>
    public void ReportSwap()
    {
        if (Context != IntPtr.Zero)
            MpvNative.mpv_render_context_report_swap(Context);
    }
}
