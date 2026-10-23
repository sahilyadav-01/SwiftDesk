using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RemoteDesk.Core;

namespace RemoteDesk.Platform.MacOS;

public sealed class MacOSPlatformAdapter : IRemotePlatformAdapter
{
    private static readonly bool IsMacOS = OperatingSystem.IsMacOS();
    private static readonly Lazy<bool> HasCoreGraphics = new(() =>
        IsMacOS && NativeLibrary.TryLoad("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics", out _));
    private static readonly Lazy<bool> HasApplicationServices = new(() =>
        IsMacOS && NativeLibrary.TryLoad("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices", out _));

    private const uint kCGHIDEventTap = 0;
    private const uint kCGEventLeftMouseDown = 1;
    private const uint kCGEventLeftMouseUp = 2;
    private const uint kCGEventRightMouseDown = 3;
    private const uint kCGEventRightMouseUp = 4;
    private const uint kCGEventMouseMoved = 5;
    private const uint kCGEventScrollWheel = 22;

    public ValueTask<PlatformCapabilityReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requirements = new List<string>();

        if (!IsMacOS)
        {
            requirements.Add("This adapter can only run on macOS.");
            return ValueTask.FromResult(new PlatformCapabilityReport(
                "macOS",
                ScreenCaptureAvailable: false,
                InputControlAvailable: false,
                FileTransferAvailable: false,
                RebootAvailable: false,
                requirements));
        }

        var screenCaptureAvailable = false;
        var inputControlAvailable = false;

        if (HasCoreGraphics.Value)
        {
            var hasScreenPermission = CoreGraphicsInterop.CGPreflightScreenCaptureAccess();
            if (hasScreenPermission)
            {
                screenCaptureAvailable = true;
                requirements.Add("Screen Recording permission granted via CoreGraphics / ScreenCaptureKit.");
            }
            else
            {
                requirements.Add("Screen Recording permission is required in System Settings -> Privacy & Security.");
            }
        }
        else
        {
            requirements.Add("CoreGraphics framework is not available.");
        }

        if (HasApplicationServices.Value)
        {
            var hasAccessibility = CoreGraphicsInterop.AXIsProcessTrusted();
            if (hasAccessibility)
            {
                inputControlAvailable = true;
                requirements.Add("Accessibility permission granted for synthetic input injection.");
            }
            else
            {
                requirements.Add("Accessibility permission is required in System Settings -> Privacy & Security -> Accessibility.");
            }
        }
        else
        {
            requirements.Add("ApplicationServices framework is not available.");
        }

        return ValueTask.FromResult(new PlatformCapabilityReport(
            "macOS",
            ScreenCaptureAvailable: screenCaptureAvailable,
            InputControlAvailable: inputControlAvailable,
            FileTransferAvailable: true,
            RebootAvailable: false,
            requirements));
    }

    public async IAsyncEnumerable<ScreenFrame> CaptureAsync(
        int framesPerSecond,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsMacOS)
        {
            throw new PlatformNotSupportedException("macOS screen capture is only supported on macOS.");
        }

        if (!HasCoreGraphics.Value)
        {
            throw new PlatformNotSupportedException("CoreGraphics is required for macOS screen capture.");
        }

        if (!CoreGraphicsInterop.CGPreflightScreenCaptureAccess())
        {
            CoreGraphicsInterop.CGRequestScreenCaptureAccess();
            throw new UnauthorizedAccessException("Screen Recording permission has not been granted for this application.");
        }

        var targetFps = Math.Clamp(framesPerSecond, 1, 60);
        var intervalMs = 1000 / targetFps;
        long sequence = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var frameStart = Environment.TickCount64;

            var mainDisplay = CoreGraphicsInterop.CGMainDisplayID();
            var imageRef = CoreGraphicsInterop.CGDisplayCreateImage(mainDisplay);

            if (imageRef != IntPtr.Zero)
            {
                try
                {
                    var width = (int)CoreGraphicsInterop.CGImageGetWidth(imageRef);
                    var height = (int)CoreGraphicsInterop.CGImageGetHeight(imageRef);
                    var provider = CoreGraphicsInterop.CGImageGetDataProvider(imageRef);

                    if (provider != IntPtr.Zero)
                    {
                        var dataRef = CoreGraphicsInterop.CGDataProviderCopyData(provider);
                        if (dataRef != IntPtr.Zero)
                        {
                            try
                            {
                                var length = (int)CoreGraphicsInterop.CFDataGetLength(dataRef);
                                var bytePtr = CoreGraphicsInterop.CFDataGetBytePtr(dataRef);

                                if (length > 0 && bytePtr != IntPtr.Zero)
                                {
                                    var pixelBytes = new byte[length];
                                    Marshal.Copy(bytePtr, pixelBytes, 0, length);

                                    var bmpStream = WrapBgraInBmp(pixelBytes, width, height);

                                    yield return new ScreenFrame(
                                        Interlocked.Increment(ref sequence),
                                        width,
                                        height,
                                        "Bmp",
                                        bmpStream,
                                        DateTimeOffset.UtcNow);
                                }
                            }
                            finally
                            {
                                CoreGraphicsInterop.CFRelease(dataRef);
                            }
                        }
                    }
                }
                finally
                {
                    CoreGraphicsInterop.CFRelease(imageRef);
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

    public ValueTask InjectInputAsync(RemoteInput input, CancellationToken cancellationToken = default)
    {
        if (!IsMacOS)
        {
            throw new PlatformNotSupportedException("macOS input injection is only supported on macOS.");
        }

        if (!HasApplicationServices.Value)
        {
            throw new PlatformNotSupportedException("ApplicationServices is required for macOS input injection.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var display = CoreGraphicsInterop.CGMainDisplayID();
        var bounds = CoreGraphicsInterop.CGDisplayBounds(display);
        var screenWidth = Math.Max(1.0, bounds.size.width);
        var screenHeight = Math.Max(1.0, bounds.size.height);

        switch (input.Kind)
        {
            case RemoteInputKind.PointerMove:
            {
                var point = new CGPoint
                {
                    x = Math.Clamp(input.NormalizedX, 0.0, 1.0) * screenWidth,
                    y = Math.Clamp(input.NormalizedY, 0.0, 1.0) * screenHeight,
                };
                var ev = CoreGraphicsInterop.CGEventCreateMouseEvent(IntPtr.Zero, kCGEventMouseMoved, point, 0);
                if (ev != IntPtr.Zero)
                {
                    CoreGraphicsInterop.CGEventPost(kCGHIDEventTap, ev);
                    CoreGraphicsInterop.CFRelease(ev);
                }
                break;
            }

            case RemoteInputKind.PointerButtonDown:
            case RemoteInputKind.PointerButtonUp:
            {
                var isDown = input.Kind == RemoteInputKind.PointerButtonDown;
                var isRight = input.Key == "Right";
                var isMiddle = input.Key == "Middle";

                var point = new CGPoint
                {
                    x = Math.Clamp(input.NormalizedX, 0.0, 1.0) * screenWidth,
                    y = Math.Clamp(input.NormalizedY, 0.0, 1.0) * screenHeight,
                };

                uint eventType;
                uint button;

                if (isRight)
                {
                    eventType = isDown ? kCGEventRightMouseDown : kCGEventRightMouseUp;
                    button = 1;
                }
                else if (isMiddle)
                {
                    eventType = isDown ? kCGEventLeftMouseDown : kCGEventLeftMouseUp;
                    button = 2;
                }
                else
                {
                    eventType = isDown ? kCGEventLeftMouseDown : kCGEventLeftMouseUp;
                    button = 0;
                }

                var ev = CoreGraphicsInterop.CGEventCreateMouseEvent(IntPtr.Zero, eventType, point, button);
                if (ev != IntPtr.Zero)
                {
                    CoreGraphicsInterop.CGEventPost(kCGHIDEventTap, ev);
                    CoreGraphicsInterop.CFRelease(ev);
                }
                break;
            }

            case RemoteInputKind.MouseWheel:
            {
                var wheelDelta = input.WheelDelta > 0 ? 3 : -3;
                var ev = CoreGraphicsInterop.CGEventCreateScrollWheelEvent(IntPtr.Zero, 0, 1, wheelDelta);
                if (ev != IntPtr.Zero)
                {
                    CoreGraphicsInterop.CGEventPost(kCGHIDEventTap, ev);
                    CoreGraphicsInterop.CFRelease(ev);
                }
                break;
            }

            case RemoteInputKind.KeyDown:
            case RemoteInputKind.KeyUp:
            {
                if (!string.IsNullOrEmpty(input.Key))
                {
                    var isDown = input.Kind == RemoteInputKind.KeyDown;
                    var keycode = ResolveMacVirtualKey(input.Key);
                    if (keycode != 0xFFFF)
                    {
                        var ev = CoreGraphicsInterop.CGEventCreateKeyboardEvent(IntPtr.Zero, keycode, isDown);
                        if (ev != IntPtr.Zero)
                        {
                            CoreGraphicsInterop.CGEventPost(kCGHIDEventTap, ev);
                            CoreGraphicsInterop.CFRelease(ev);
                        }
                    }
                }
                break;
            }
        }

        return ValueTask.CompletedTask;
    }

    private static ushort ResolveMacVirtualKey(string key) =>
        key.ToUpperInvariant() switch
        {
            "RETURN" or "ENTER" => 36,
            "TAB" => 48,
            "SPACE" => 49,
            "BACKSPACE" or "BACK" or "DELETE" => 51,
            "ESCAPE" or "ESC" => 53,
            "LEFT" => 123,
            "RIGHT" => 124,
            "DOWN" => 125,
            "UP" => 126,
            "A" => 0,
            "B" => 11,
            "C" => 8,
            "D" => 2,
            "E" => 14,
            "F" => 3,
            "G" => 5,
            "H" => 4,
            "I" => 34,
            "J" => 38,
            "K" => 40,
            "L" => 37,
            "M" => 46,
            "N" => 45,
            "O" => 31,
            "P" => 35,
            "Q" => 12,
            "R" => 15,
            "S" => 1,
            "T" => 17,
            "U" => 32,
            "V" => 9,
            "W" => 13,
            "X" => 7,
            "Y" => 16,
            "Z" => 6,
            _ => 0xFFFF,
        };

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
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2, 4), fileSize);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10, 4), totalHeaderSize);

        // BITMAPINFOHEADER
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14, 4), infoHeaderSize);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22, 4), -height); // Top-down
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26, 2), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28, 2), 32);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(30, 4), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34, 4), bgraPixels.Length);

        // Copy pixels
        Buffer.BlockCopy(bgraPixels, 0, bmp, totalHeaderSize, bgraPixels.Length);

        return bmp;
    }

    public ValueTask RebootAsync(string reason, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException("Remote reboot requires a separately audited privileged helper."));

    [StructLayout(LayoutKind.Sequential)]
    public struct CGPoint
    {
        public double x;
        public double y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CGSize
    {
        public double width;
        public double height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CGRect
    {
        public CGPoint origin;
        public CGSize size;
    }

    private static class CoreGraphicsInterop
    {
        private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const string ApplicationServicesLib = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

        [DllImport(CoreGraphicsLib, ExactSpelling = true)]
        public static extern bool CGPreflightScreenCaptureAccess();

        [DllImport(CoreGraphicsLib, ExactSpelling = true)]
        public static extern bool CGRequestScreenCaptureAccess();

        [DllImport(ApplicationServicesLib, ExactSpelling = true)]
        public static extern bool AXIsProcessTrusted();

        [DllImport(CoreGraphicsLib, ExactSpelling = true)]
        public static extern uint CGMainDisplayID();

        [DllImport(CoreGraphicsLib, ExactSpelling = true)]
        public static extern CGRect CGDisplayBounds(uint display);

        [DllImport(CoreGraphicsLib, ExactSpelling = true)]
        public static extern IntPtr CGDisplayCreateImage(uint displayID);

        [DllImport(CoreGraphicsLib, ExactSpelling = true)]
        public static extern nuint CGImageGetWidth(IntPtr image);

        [DllImport(CoreGraphicsLib, ExactSpelling = true)]
        public static extern nuint CGImageGetHeight(IntPtr image);

        [DllImport(CoreGraphicsLib, ExactSpelling = true)]
        public static extern IntPtr CGImageGetDataProvider(IntPtr image);

        [DllImport(CoreGraphicsLib, ExactSpelling = true)]
        public static extern IntPtr CGDataProviderCopyData(IntPtr provider);

        [DllImport(CoreGraphicsLib, ExactSpelling = true)]
        public static extern nint CFDataGetLength(IntPtr theData);

        [DllImport(CoreGraphicsLib, ExactSpelling = true)]
        public static extern IntPtr CFDataGetBytePtr(IntPtr theData);

        [DllImport(CoreGraphicsLib, ExactSpelling = true)]
        public static extern void CFRelease(IntPtr cf);

        [DllImport(CoreGraphicsLib, ExactSpelling = true)]
        public static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint mouseType, CGPoint mouseCursorPosition, uint mouseButton);

        [DllImport(CoreGraphicsLib, ExactSpelling = true)]
        public static extern IntPtr CGEventCreateScrollWheelEvent(IntPtr source, uint units, uint wheelCount, int wheel1);

        [DllImport(CoreGraphicsLib, ExactSpelling = true)]
        public static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, bool keyDown);

        [DllImport(CoreGraphicsLib, ExactSpelling = true)]
        public static extern void CGEventPost(uint tapLocation, IntPtr eventRef);
    }
}
