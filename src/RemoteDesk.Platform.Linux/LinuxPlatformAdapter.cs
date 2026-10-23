using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RemoteDesk.Core;

namespace RemoteDesk.Platform.Linux;

public sealed class LinuxPlatformAdapter(
    IPrivilegedRebootClient? rebootClient = null,
    IWaylandPortalCoordinator? waylandCoordinator = null) : IRemotePlatformAdapter
{
    private readonly IPrivilegedRebootClient? _rebootClient = rebootClient;
    private readonly IWaylandPortalCoordinator _waylandCoordinator = waylandCoordinator ?? new WaylandPortalCoordinator();
    private const int ZPixmap = 2;
    private const ulong AllPlanes = ~0UL;

    private static readonly bool IsLinux = OperatingSystem.IsLinux();
    private static readonly Lazy<bool> HasLibX11 = new(() =>
        IsLinux && (NativeLibrary.TryLoad("libX11.so.6", out _) || NativeLibrary.TryLoad("libX11.so", out _)));
    private static readonly Lazy<bool> HasLibXtst = new(() =>
        IsLinux && (NativeLibrary.TryLoad("libXtst.so.6", out _) || NativeLibrary.TryLoad("libXtst.so", out _)));

    public ValueTask<PlatformCapabilityReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requirements = new List<string>();

        var waylandStatus = _waylandCoordinator.Probe();
        if (waylandStatus.IsWaylandSession)
        {
            var captureAvailable = waylandStatus.ScreenCastAvailable;
            var inputAvailable = waylandStatus.RemoteDesktopAvailable;

            if (captureAvailable)
            {
                requirements.Add("Wayland ScreenCast portal (org.freedesktop.portal.ScreenCast via PipeWire) available.");
            }
            else
            {
                requirements.Add("Wayland ScreenCast portal requires active D-Bus session.");
            }

            if (inputAvailable)
            {
                requirements.Add("Wayland RemoteDesktop synthetic input portal (org.freedesktop.portal.RemoteDesktop / libei) available.");
            }
            else
            {
                requirements.Add("Wayland RemoteDesktop input portal requires active D-Bus session.");
            }

            var rebootAvailable = _rebootClient?.IsAvailable == true;
            if (rebootAvailable)
            {
                requirements.Add("Privileged remote reboot enabled via dedicated SwiftDesk reboot service.");
            }
            else
            {
                requirements.Add("Privileged remote reboot service is not installed or configured on this host.");
            }

            return ValueTask.FromResult(new PlatformCapabilityReport(
                "Linux (Wayland)",
                ScreenCaptureAvailable: captureAvailable,
                InputControlAvailable: inputAvailable,
                FileTransferAvailable: true,
                RebootAvailable: rebootAvailable,
                requirements));
        }

        if (!IsLinux)
        {
            requirements.Add("This adapter can only run on Linux.");
            return ValueTask.FromResult(new PlatformCapabilityReport(
                "Linux",
                ScreenCaptureAvailable: false,
                InputControlAvailable: false,
                FileTransferAvailable: false,
                RebootAvailable: false,
                requirements));
        }

        var hasDisplay = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

        if (hasDisplay && HasLibX11.Value)
        {
            requirements.Add("X11 screen capture enabled via libX11 XGetImage.");
            var inputAvailable = HasLibXtst.Value;
            if (inputAvailable)
            {
                requirements.Add("X11 input injection enabled via libXtst XTest extension.");
            }
            else
            {
                requirements.Add("libXtst not found; input injection is disabled.");
            }

            var rebootAvailable = _rebootClient?.IsAvailable == true;
            if (rebootAvailable)
            {
                requirements.Add("Privileged remote reboot enabled via dedicated SwiftDesk reboot service.");
            }
            else
            {
                requirements.Add("Privileged remote reboot service is not installed or configured on this host.");
            }

            return ValueTask.FromResult(new PlatformCapabilityReport(
                "Linux (X11)",
                ScreenCaptureAvailable: true,
                InputControlAvailable: inputAvailable,
                FileTransferAvailable: true,
                RebootAvailable: rebootAvailable,
                requirements));
        }

        requirements.Add("Headless Linux environment: No active DISPLAY or WAYLAND_DISPLAY found.");
        return ValueTask.FromResult(new PlatformCapabilityReport(
            "Linux (Headless)",
            ScreenCaptureAvailable: false,
            InputControlAvailable: false,
            FileTransferAvailable: true,
            RebootAvailable: _rebootClient?.IsAvailable == true,
            requirements));
    }

    public async IAsyncEnumerable<ScreenFrame> CaptureAsync(
        int framesPerSecond,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsLinux)
        {
            throw new PlatformNotSupportedException("Linux screen capture is only supported on Linux.");
        }

        if (!HasLibX11.Value)
        {
            throw new PlatformNotSupportedException("libX11 is not available for X11 screen capture.");
        }

        var display = X11Interop.XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to open X11 display. Ensure DISPLAY environment variable is set.");
        }

        var targetFps = Math.Clamp(framesPerSecond, 1, 60);
        var intervalMs = 1000 / targetFps;
        long sequence = 0;

        try
        {
            var rootWindow = X11Interop.XDefaultRootWindow(display);
            var attributes = new X11Interop.XWindowAttributes();
            if (X11Interop.XGetWindowAttributes(display, rootWindow, ref attributes) == 0)
            {
                throw new InvalidOperationException("Failed to retrieve X11 root window dimensions.");
            }

            var width = attributes.width;
            var height = attributes.height;

            while (!cancellationToken.IsCancellationRequested)
            {
                var frameStart = Environment.TickCount64;

                var ximagePtr = X11Interop.XGetImage(
                    display,
                    rootWindow,
                    0,
                    0,
                    (uint)width,
                    (uint)height,
                    AllPlanes,
                    ZPixmap);

                if (ximagePtr != IntPtr.Zero)
                {
                    try
                    {
                        var ximage = Marshal.PtrToStructure<X11Interop.XImage>(ximagePtr);
                        var dataLength = ximage.bytes_per_line * ximage.height;
                        var pixelBytes = new byte[dataLength];
                        Marshal.Copy(ximage.data, pixelBytes, 0, dataLength);

                        var bmpStream = WrapBgraInBmp(pixelBytes, width, height);

                        yield return new ScreenFrame(
                            Interlocked.Increment(ref sequence),
                            width,
                            height,
                            "Bmp",
                            bmpStream,
                            DateTimeOffset.UtcNow);
                    }
                    finally
                    {
                        _ = X11Interop.XDestroyImage(ximagePtr);
                    }
                }

                var elapsed = (int)(Environment.TickCount64 - frameStart);
                var remaining = intervalMs - elapsed;
                if (remaining > 0)
                {
                    await Task.Delay(remaining, cancellationToken);
                }
                else
                {
                    await Task.Yield();
                }
            }
        }
        finally
        {
            _ = X11Interop.XCloseDisplay(display);
        }
    }

    public ValueTask InjectInputAsync(RemoteInput input, CancellationToken cancellationToken = default)
    {
        if (!IsLinux)
        {
            throw new PlatformNotSupportedException("Linux input injection is only supported on Linux.");
        }

        if (_waylandCoordinator.Probe().IsWaylandSession)
        {
            return _waylandCoordinator.InjectInputAsync(input, cancellationToken);
        }

        if (!HasLibXtst.Value)
        {
            throw new PlatformNotSupportedException("libXtst is required for X11 synthetic input injection.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var display = X11Interop.XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            return ValueTask.FromException(new InvalidOperationException("Failed to open X11 display."));
        }

        try
        {
            var rootWindow = X11Interop.XDefaultRootWindow(display);
            var attributes = new X11Interop.XWindowAttributes();
            _ = X11Interop.XGetWindowAttributes(display, rootWindow, ref attributes);
            var screenWidth = Math.Max(1, attributes.width);
            var screenHeight = Math.Max(1, attributes.height);

            switch (input.Kind)
            {
                case RemoteInputKind.PointerMove:
                {
                    var x = (int)(Math.Clamp(input.NormalizedX, 0.0, 1.0) * screenWidth);
                    var y = (int)(Math.Clamp(input.NormalizedY, 0.0, 1.0) * screenHeight);
                    _ = X11Interop.XTestFakeMotionEvent(display, -1, x, y, 0);
                    _ = X11Interop.XFlush(display);
                    break;
                }

                case RemoteInputKind.PointerButtonDown:
                case RemoteInputKind.PointerButtonUp:
                {
                    var isPress = input.Kind == RemoteInputKind.PointerButtonDown;
                    uint button = input.Key switch
                    {
                        "Right" => 3,
                        "Middle" => 2,
                        _ => 1,
                    };
                    _ = X11Interop.XTestFakeButtonEvent(display, button, isPress, 0);
                    _ = X11Interop.XFlush(display);
                    break;
                }

                case RemoteInputKind.MouseWheel:
                {
                    uint button = input.WheelDelta > 0 ? 4u : 5u;
                    _ = X11Interop.XTestFakeButtonEvent(display, button, is_press: true, 0);
                    _ = X11Interop.XTestFakeButtonEvent(display, button, is_press: false, 0);
                    _ = X11Interop.XFlush(display);
                    break;
                }

                case RemoteInputKind.KeyDown:
                case RemoteInputKind.KeyUp:
                {
                    if (!string.IsNullOrEmpty(input.Key))
                    {
                        var isPress = input.Kind == RemoteInputKind.KeyDown;
                        var keycode = ResolveX11Keycode(display, input.Key);
                        if (keycode != 0)
                        {
                            _ = X11Interop.XTestFakeKeyEvent(display, keycode, isPress, 0);
                            _ = X11Interop.XFlush(display);
                        }
                    }
                    break;
                }
            }

            return ValueTask.CompletedTask;
        }
        finally
        {
            _ = X11Interop.XCloseDisplay(display);
        }
    }

    private static uint ResolveX11Keycode(IntPtr display, string key)
    {
        ulong keysym = key.ToUpperInvariant() switch
        {
            "RETURN" or "ENTER" => 0xFF0D,
            "ESCAPE" or "ESC" => 0xFF1B,
            "BACKSPACE" or "BACK" => 0xFF08,
            "TAB" => 0xFF09,
            "SPACE" => 0x0020,
            "LEFT" => 0xFF51,
            "UP" => 0xFF52,
            "RIGHT" => 0xFF53,
            "DOWN" => 0xFF54,
            "DELETE" or "DEL" => 0xFFFF,
            "HOME" => 0xFF50,
            "END" => 0xFF57,
            "PAGEUP" => 0xFF55,
            "PAGEDOWN" => 0xFF56,
            _ when key.Length == 1 => key[0],
            _ => 0,
        };

        if (keysym == 0)
        {
            return 0;
        }

        return X11Interop.XKeysymToKeycode(display, keysym);
    }

    private static byte[] WrapBgraInBmp(byte[] bgraPixels, int width, int height)
    {
        const int fileHeaderSize = 14;
        const int infoHeaderSize = 40;
        const int totalHeaderSize = fileHeaderSize + infoHeaderSize;
        var fileSize = totalHeaderSize + bgraPixels.Length;

        var bmp = new byte[fileSize];

        // BITMAPFILEHEADER
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitivesWriteInt32LittleEndian(bmp.AsSpan(2, 4), fileSize);
        BinaryPrimitivesWriteInt32LittleEndian(bmp.AsSpan(10, 4), totalHeaderSize);

        // BITMAPINFOHEADER
        BinaryPrimitivesWriteInt32LittleEndian(bmp.AsSpan(14, 4), infoHeaderSize);
        BinaryPrimitivesWriteInt32LittleEndian(bmp.AsSpan(18, 4), width);
        BinaryPrimitivesWriteInt32LittleEndian(bmp.AsSpan(22, 4), -height); // Top-down
        BinaryPrimitivesWriteInt16LittleEndian(bmp.AsSpan(26, 2), 1); // Planes
        BinaryPrimitivesWriteInt16LittleEndian(bmp.AsSpan(28, 2), 32); // Bits per pixel
        BinaryPrimitivesWriteInt32LittleEndian(bmp.AsSpan(30, 4), 0); // BI_RGB (uncompressed)
        BinaryPrimitivesWriteInt32LittleEndian(bmp.AsSpan(34, 4), bgraPixels.Length);

        // Copy pixel data
        Buffer.BlockCopy(bgraPixels, 0, bmp, totalHeaderSize, bgraPixels.Length);

        return bmp;
    }

    private static void BinaryPrimitivesWriteInt32LittleEndian(Span<byte> destination, int value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(destination, value);

    private static void BinaryPrimitivesWriteInt16LittleEndian(Span<byte> destination, short value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(destination, value);

    public async ValueTask RebootAsync(string reason, CancellationToken cancellationToken = default)
    {
        if (_rebootClient is null || !_rebootClient.IsAvailable)
        {
            throw new NotSupportedException("Remote reboot requires the separately installed SwiftDesk privileged reboot service.");
        }

        var result = await _rebootClient.RequestRebootAsync("linux_host_session", reason, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Privileged reboot request failed: {result.Message} ({result.ErrorCode})");
        }
    }

    private static class X11Interop
    {
        [DllImport("libX11.so.6", EntryPoint = "XOpenDisplay", ExactSpelling = true)]
        public static extern IntPtr XOpenDisplay(IntPtr displayName);

        [DllImport("libX11.so.6", EntryPoint = "XCloseDisplay")]
        public static extern int XCloseDisplay(IntPtr display);

        [DllImport("libX11.so.6", EntryPoint = "XDefaultRootWindow")]
        public static extern IntPtr XDefaultRootWindow(IntPtr display);

        [DllImport("libX11.so.6", EntryPoint = "XGetWindowAttributes")]
        public static extern int XGetWindowAttributes(IntPtr display, IntPtr window, ref XWindowAttributes attributes);

        [DllImport("libX11.so.6", EntryPoint = "XGetImage")]
        public static extern IntPtr XGetImage(
            IntPtr display,
            IntPtr drawable,
            int x,
            int y,
            uint width,
            uint height,
            ulong plane_mask,
            int format);

        [DllImport("libX11.so.6", EntryPoint = "XDestroyImage")]
        public static extern int XDestroyImage(IntPtr ximage);

        [DllImport("libX11.so.6", EntryPoint = "XKeysymToKeycode")]
        public static extern byte XKeysymToKeycode(IntPtr display, ulong keysym);

        [DllImport("libX11.so.6", EntryPoint = "XFlush")]
        public static extern int XFlush(IntPtr display);

        [DllImport("libXtst.so.6", EntryPoint = "XTestFakeMotionEvent")]
        public static extern int XTestFakeMotionEvent(IntPtr display, int screen_number, int x, int y, ulong delay);

        [DllImport("libXtst.so.6", EntryPoint = "XTestFakeButtonEvent")]
        public static extern int XTestFakeButtonEvent(IntPtr display, uint button, bool is_press, ulong delay);

        [DllImport("libXtst.so.6", EntryPoint = "XTestFakeKeyEvent")]
        public static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, bool is_press, ulong delay);

        [StructLayout(LayoutKind.Sequential)]
        public struct XWindowAttributes
        {
            public int x, y;
            public int width, height;
            public int border_width;
            public int depth;
            public IntPtr visual;
            public IntPtr root;
            public int @class;
            public int bit_gravity;
            public int win_gravity;
            public int backing_store;
            public ulong backing_planes;
            public ulong backing_pixel;
            public bool save_under;
            public IntPtr colormap;
            public bool map_installed;
            public int map_state;
            public long all_event_masks;
            public long your_event_mask;
            public long do_not_propagate_mask;
            public bool override_redirect;
            public IntPtr screen;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XImage
        {
            public int width, height;
            public int xoffset;
            public int format;
            public IntPtr data;
            public int byte_order;
            public int bitmap_unit;
            public int bitmap_bit_order;
            public int bitmap_pad;
            public int depth;
            public int bytes_per_line;
            public int bits_per_pixel;
            public ulong red_mask;
            public ulong green_mask;
            public ulong blue_mask;
            public IntPtr obdata;
        }
    }
}
