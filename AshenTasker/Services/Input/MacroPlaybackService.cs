using System.Runtime.InteropServices;
using System.Windows.Input;
using AshenTasker.Models.Macros;
using AshenTasker.Models.Windowing;
using AshenTasker.Services.Windowing;

namespace AshenTasker.Services.Input;

public sealed class MacroPlaybackService(WindowEnumerationService windowEnumerationService)
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseEventMove = 0x0001;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint MouseEventWheel = 0x0800;
    private const uint KeyEventKeyUp = 0x0002;
    private static readonly SemaphoreSlim InputLock = new(1, 1);

    public async Task PlayAsync(MacroDocument document, IReadOnlyList<WindowInfo> targets, double speed, CancellationToken cancellationToken)
    {
        if (document.Actions.Count == 0 || targets.Count == 0)
        {
            return;
        }

        List<MacroAction> actions = document.Actions.OrderBy(action => action.TimeMs).ToList();
        List<Task> targetTasks = targets.Select(target => PlayForTargetAsync(actions, target, speed, cancellationToken)).ToList();
        await Task.WhenAll(targetTasks);
    }

    private async Task PlayForTargetAsync(List<MacroAction> actions, WindowInfo target, double speed, CancellationToken cancellationToken)
    {
        long previousTimeMs = 0;
        int? currentX = null;
        int? currentY = null;

        foreach (MacroAction action in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long delayMs = Math.Max(0, action.TimeMs - previousTimeMs);
            previousTimeMs = action.TimeMs;

            if (delayMs > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs / Math.Max(0.001, speed)), cancellationToken);
            }

            if (!windowEnumerationService.TryGetClientBounds(target, out WindowBounds clientBounds))
            {
                continue;
            }

            (int x, int y)? clientPoint = ResolveClientPoint(action, clientBounds);

            await InputLock.WaitAsync(cancellationToken);
            try
            {
                SetForegroundWindow(target.Handle);

                if (clientPoint is { } point)
                {
                    int screenX = clientBounds.Left + point.x;
                    int screenY = clientBounds.Top + point.y;

                    if (action.Type == MacroActionKind.MouseMove)
                    {
                        await MoveSmoothAsync(currentX ?? screenX, currentY ?? screenY, screenX, screenY, cancellationToken);
                    }
                    else
                    {
                        SetCursorPos(screenX, screenY);
                    }

                    currentX = screenX;
                    currentY = screenY;
                }

                SendAction(action);
            }
            finally
            {
                InputLock.Release();
            }
        }
    }

    private static async Task MoveSmoothAsync(int startX, int startY, int endX, int endY, CancellationToken cancellationToken)
    {
        double distance = Math.Sqrt(Math.Pow(endX - startX, 2) + Math.Pow(endY - startY, 2));
        int steps = Math.Clamp((int)(distance / 18), 1, 36);

        for (int i = 1; i <= steps; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double t = i / (double)steps;
            double eased = t * t * (3 - 2 * t);
            int x = (int)Math.Round(startX + ((endX - startX) * eased));
            int y = (int)Math.Round(startY + ((endY - startY) * eased));
            SetCursorPos(x, y);

            if (steps > 1)
            {
                await Task.Delay(4, cancellationToken);
            }
        }
    }

    private static (int x, int y)? ResolveClientPoint(MacroAction action, WindowBounds clientBounds)
    {
        if (action.X is null || action.Y is null)
        {
            return null;
        }

        int x = Math.Clamp((int)Math.Round(action.X.Value * clientBounds.Width), 0, Math.Max(0, clientBounds.Width - 1));
        int y = Math.Clamp((int)Math.Round(action.Y.Value * clientBounds.Height), 0, Math.Max(0, clientBounds.Height - 1));
        return (x, y);
    }

    private static void SendAction(MacroAction action)
    {
        switch (action.Type)
        {
            case MacroActionKind.MouseButtonDown:
            case MacroActionKind.MouseButtonUp:
                SendMouseButton(action.Button ?? MacroMouseButton.Left, action.Type == MacroActionKind.MouseButtonDown);
                break;
            case MacroActionKind.MouseWheel:
                SendMouse(MouseEventWheel, unchecked((uint)(action.WheelDelta ?? 0)));
                break;
            case MacroActionKind.KeyDown:
            case MacroActionKind.KeyUp:
                SendKey(action.VirtualKey, action.Key, action.Type == MacroActionKind.KeyUp);
                break;
        }
    }

    private static void SendMouseButton(MacroMouseButton button, bool isDown)
    {
        uint flags = button switch
        {
            MacroMouseButton.Right => isDown ? MouseEventRightDown : MouseEventRightUp,
            MacroMouseButton.Middle => isDown ? MouseEventMiddleDown : MouseEventMiddleUp,
            _ => isDown ? MouseEventLeftDown : MouseEventLeftUp
        };

        SendMouse(flags, 0);
    }

    private static void SendMouse(uint flags, uint mouseData)
    {
        NativeInput[] inputs =
        [
            new()
            {
                Type = InputMouse,
                Mouse = new NativeMouseInput
                {
                    MouseData = mouseData,
                    Flags = flags
                }
            }
        ];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
    }

    private static void SendKey(int? virtualKey, string? keyName, bool isUp)
    {
        int key = virtualKey ?? TryResolveVirtualKey(keyName);
        if (key <= 0)
        {
            return;
        }

        NativeInput[] inputs =
        [
            new()
            {
                Type = InputKeyboard,
                Keyboard = new NativeKeyboardInput
                {
                    VirtualKey = (ushort)key,
                    Flags = isUp ? KeyEventKeyUp : 0
                }
            }
        ];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
    }

    private static int TryResolveVirtualKey(string? keyName)
    {
        return Enum.TryParse(keyName, out Key key)
            ? KeyInterop.VirtualKeyFromKey(key)
            : 0;
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Data;

        public NativeMouseInput Mouse
        {
            set => Data.Mouse = value;
        }

        public NativeKeyboardInput Keyboard
        {
            set => Data.Keyboard = value;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)]
        public NativeMouseInput Mouse;

        [FieldOffset(0)]
        public NativeKeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }
}
