using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteDesk.Core;

namespace RemoteDesk.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsPlatformAdapter(IPrivilegedRebootClient? rebootClient = null) : IRemotePlatformAdapter
{
    private readonly IPrivilegedRebootClient? _rebootClient = rebootClient;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEKEYBDHARDWAREINPUT mkhi;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct MOUSEKEYBDHARDWAREINPUT
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    public ValueTask<PlatformCapabilityReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requirements = new List<string>();

        if (!OperatingSystem.IsWindows())
        {
            requirements.Add("This adapter can only run on Windows.");
            return ValueTask.FromResult(new PlatformCapabilityReport(
                "Windows",
                ScreenCaptureAvailable: false,
                InputControlAvailable: false,
                FileTransferAvailable: false,
                RebootAvailable: false,
                requirements));
        }

        requirements.Add("Screen capture enabled via desktop surface capture (GDI/Bitmap).");
        requirements.Add("Input control enabled via Windows SendInput API.");

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
            "Windows",
            ScreenCaptureAvailable: true,
            InputControlAvailable: true,
            FileTransferAvailable: true,
            RebootAvailable: rebootAvailable,
            requirements));
    }

    public async IAsyncEnumerable<ScreenFrame> CaptureAsync(
        int framesPerSecond,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows screen capture is only supported on Windows.");
        }

        var targetFps = Math.Clamp(framesPerSecond, 1, 60);
        var intervalMs = 1000 / targetFps;
        long sequence = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var frameStart = Environment.TickCount64;

            int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            if (width <= 0 || height <= 0)
            {
                left = 0;
                top = 0;
                width = GetSystemMetrics(SM_CXSCREEN);
                height = GetSystemMetrics(SM_CYSCREEN);
            }

            if (width <= 0 || height <= 0)
            {
                width = 1920;
                height = 1080;
            }

            ScreenFrame frame;
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                }

                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Jpeg);
                frame = new ScreenFrame(
                    Interlocked.Increment(ref sequence),
                    width,
                    height,
                    "Jpeg",
                    stream.ToArray(),
                    DateTimeOffset.UtcNow);
            }

            yield return frame;

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
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows input injection is only supported on Windows.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var inputs = new INPUT[1];
        var cbSize = Marshal.SizeOf<INPUT>();

        switch (input.Kind)
        {
            case RemoteInputKind.PointerMove:
            {
                var normX = Math.Clamp(input.NormalizedX, 0.0, 1.0);
                var normY = Math.Clamp(input.NormalizedY, 0.0, 1.0);
                inputs[0] = new INPUT
                {
                    type = INPUT_MOUSE,
                    mkhi = new MOUSEKEYBDHARDWAREINPUT
                    {
                        mi = new MOUSEINPUT
                        {
                            dx = (int)(normX * 65535.0),
                            dy = (int)(normY * 65535.0),
                            dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                        },
                    },
                };
                SendInput(1, inputs, cbSize);
                break;
            }

            case RemoteInputKind.PointerButtonDown:
            case RemoteInputKind.PointerButtonUp:
            {
                var isDown = input.Kind == RemoteInputKind.PointerButtonDown;
                var buttonFlag = input.Key switch
                {
                    "Right" => isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
                    "Middle" => isDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
                    _ => isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
                };

                inputs[0] = new INPUT
                {
                    type = INPUT_MOUSE,
                    mkhi = new MOUSEKEYBDHARDWAREINPUT
                    {
                        mi = new MOUSEINPUT
                        {
                            dwFlags = buttonFlag,
                        },
                    },
                };
                SendInput(1, inputs, cbSize);
                break;
            }

            case RemoteInputKind.MouseWheel:
            {
                inputs[0] = new INPUT
                {
                    type = INPUT_MOUSE,
                    mkhi = new MOUSEKEYBDHARDWAREINPUT
                    {
                        mi = new MOUSEINPUT
                        {
                            mouseData = unchecked((uint)input.WheelDelta),
                            dwFlags = MOUSEEVENTF_WHEEL,
                        },
                    },
                };
                SendInput(1, inputs, cbSize);
                break;
            }

            case RemoteInputKind.KeyDown:
            case RemoteInputKind.KeyUp:
            {
                if (!string.IsNullOrEmpty(input.Key))
                {
                    var isKeyUp = input.Kind == RemoteInputKind.KeyUp;
                    var (vk, unicode) = ResolveKey(input.Key);

                    if (vk != 0)
                    {
                        inputs[0] = new INPUT
                        {
                            type = INPUT_KEYBOARD,
                            mkhi = new MOUSEKEYBDHARDWAREINPUT
                            {
                                ki = new KEYBDINPUT
                                {
                                    wVk = vk,
                                    dwFlags = isKeyUp ? KEYEVENTF_KEYUP : 0,
                                },
                            },
                        };
                    }
                    else if (unicode != 0)
                    {
                        inputs[0] = new INPUT
                        {
                            type = INPUT_KEYBOARD,
                            mkhi = new MOUSEKEYBDHARDWAREINPUT
                            {
                                ki = new KEYBDINPUT
                                {
                                    wScan = unicode,
                                    dwFlags = KEYEVENTF_UNICODE | (isKeyUp ? KEYEVENTF_KEYUP : 0),
                                },
                            },
                        };
                    }

                    if (inputs[0].type != 0)
                    {
                        SendInput(1, inputs, cbSize);
                    }
                }
                break;
            }
        }

        return ValueTask.CompletedTask;
    }

    private static (ushort VirtualKey, ushort UnicodeChar) ResolveKey(string key) =>
        key.ToUpperInvariant() switch
        {
            "ENTER" or "RETURN" => (0x0D, 0),
            "BACKSPACE" or "BACK" => (0x08, 0),
            "TAB" => (0x09, 0),
            "ESCAPE" or "ESC" => (0x1B, 0),
            "SPACE" => (0x20, 0),
            "LEFT" => (0x25, 0),
            "UP" => (0x26, 0),
            "RIGHT" => (0x27, 0),
            "DOWN" => (0x28, 0),
            "DELETE" or "DEL" => (0x2E, 0),
            "HOME" => (0x24, 0),
            "END" => (0x23, 0),
            "PAGEUP" => (0x21, 0),
            "PAGEDOWN" => (0x22, 0),
            _ when key.Length == 1 => (0, key[0]),
            _ => (0, 0),
        };

    public async ValueTask RebootAsync(string reason, CancellationToken cancellationToken = default)
    {
        if (_rebootClient is null || !_rebootClient.IsAvailable)
        {
            throw new NotSupportedException("Remote reboot requires the separately installed SwiftDesk privileged reboot service.");
        }

        var result = await _rebootClient.RequestRebootAsync("windows_host_session", reason, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Privileged reboot request failed: {result.Message} ({result.ErrorCode})");
        }
    }
}
