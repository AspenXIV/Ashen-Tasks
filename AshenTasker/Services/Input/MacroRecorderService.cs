using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using AshenTasker.Models.Macros;
using AshenTasker.Models.Windowing;
using AshenTasker.Services.Windowing;

namespace AshenTasker.Services.Input;

public sealed class MacroRecorderService : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private readonly WindowEnumerationService _windowEnumerationService;
    private readonly LowLevelMouseProc _mouseProc;
    private readonly LowLevelKeyboardProc _keyboardProc;
    private readonly List<MacroAction> _actions = [];
    private readonly Stopwatch _stopwatch = new();
    private IReadOnlyList<WindowInfo> _targets = [];
    private nint _mouseHook;
    private nint _keyboardHook;
    private long _lastMouseMoveMs = -1;

    public MacroRecorderService(WindowEnumerationService windowEnumerationService)
    {
        _windowEnumerationService = windowEnumerationService;
        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;
    }

    public bool IsRecording { get; private set; }

    public IReadOnlyList<MacroAction> Actions => _actions;

    public void Start(IReadOnlyList<WindowInfo> targets)
    {
        Stop();
        _targets = targets;
        _actions.Clear();
        _lastMouseMoveMs = -1;
        _stopwatch.Restart();
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, GetModuleHandle(null), 0);
        IsRecording = _mouseHook != nint.Zero || _keyboardHook != nint.Zero;
    }

    public MacroDocument StopToDocument(string name)
    {
        Stop();
        return new MacroDocument
        {
            Name = name,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            Actions = _actions.ToList()
        };
    }

    public void Stop()
    {
        if (_mouseHook != nint.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = nint.Zero;
        }

        if (_keyboardHook != nint.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = nint.Zero;
        }

        _stopwatch.Stop();
        IsRecording = false;
    }

    public void Dispose()
    {
        Stop();
    }

    private nint MouseHookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && IsRecording)
        {
            MouseHookInfo info = Marshal.PtrToStructure<MouseHookInfo>(lParam);
            if (TryResolveTargetPoint(info.Point.X, info.Point.Y, out WindowInfo target, out WindowClientPoint point))
            {
                int message = wParam.ToInt32();
                MacroAction? action = CreateMouseAction(message, info, target, point);
                if (action is not null)
                {
                    _actions.Add(action);
                }
            }
        }

        return CallNextHookEx(nint.Zero, code, wParam, lParam);
    }

    private MacroAction? CreateMouseAction(int message, MouseHookInfo info, WindowInfo target, WindowClientPoint point)
    {
        long timeMs = _stopwatch.ElapsedMilliseconds;

        if (message == WmMouseMove)
        {
            if (_lastMouseMoveMs >= 0 && timeMs - _lastMouseMoveMs < 12)
            {
                return null;
            }

            _lastMouseMoveMs = timeMs;
        }

        MacroAction action = new()
        {
            Type = message switch
            {
                WmMouseMove => MacroActionKind.MouseMove,
                WmLButtonDown or WmRButtonDown or WmMButtonDown => MacroActionKind.MouseButtonDown,
                WmLButtonUp or WmRButtonUp or WmMButtonUp => MacroActionKind.MouseButtonUp,
                WmMouseWheel => MacroActionKind.MouseWheel,
                _ => MacroActionKind.MouseMove
            },
            TimeMs = timeMs,
            Target = target.DisplayName,
            X = point.NormalizedX,
            Y = point.NormalizedY
        };

        action.Button = message switch
        {
            WmLButtonDown or WmLButtonUp => MacroMouseButton.Left,
            WmRButtonDown or WmRButtonUp => MacroMouseButton.Right,
            WmMButtonDown or WmMButtonUp => MacroMouseButton.Middle,
            _ => null
        };

        if (message == WmMouseWheel)
        {
            action.WheelDelta = unchecked((short)((info.MouseData >> 16) & 0xFFFF));
        }

        return action;
    }

    private nint KeyboardHookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && IsRecording && TryResolveForegroundTarget(out WindowInfo target))
        {
            KeyboardHookInfo info = Marshal.PtrToStructure<KeyboardHookInfo>(lParam);
            int message = wParam.ToInt32();

            if (message is WmKeyDown or WmSysKeyDown or WmKeyUp or WmSysKeyUp)
            {
                Key key = KeyInterop.KeyFromVirtualKey((int)info.VirtualKey);
                _actions.Add(new MacroAction
                {
                    Type = message is WmKeyDown or WmSysKeyDown ? MacroActionKind.KeyDown : MacroActionKind.KeyUp,
                    TimeMs = _stopwatch.ElapsedMilliseconds,
                    Target = target.DisplayName,
                    Key = key.ToString(),
                    VirtualKey = (int)info.VirtualKey
                });
            }
        }

        return CallNextHookEx(nint.Zero, code, wParam, lParam);
    }

    private bool TryResolveTargetPoint(int screenX, int screenY, out WindowInfo target, out WindowClientPoint point)
    {
        foreach (WindowInfo candidate in _targets)
        {
            if (!_windowEnumerationService.TryGetClientBounds(candidate, out WindowBounds bounds)
                || screenX < bounds.Left
                || screenX >= bounds.Right
                || screenY < bounds.Top
                || screenY >= bounds.Bottom)
            {
                continue;
            }

            int clientX = screenX - bounds.Left;
            int clientY = screenY - bounds.Top;
            target = candidate;
            point = new WindowClientPoint(clientX, clientY, clientX / (double)bounds.Width, clientY / (double)bounds.Height);
            return true;
        }

        target = null!;
        point = default;
        return false;
    }

    private bool TryResolveForegroundTarget(out WindowInfo target)
    {
        nint foreground = GetForegroundWindow();
        target = _targets.FirstOrDefault(candidate => candidate.Handle == foreground)!;
        return target is not null;
    }

    private delegate nint LowLevelMouseProc(int code, nint wParam, nint lParam);

    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hookId, Delegate callback, nint moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hookHandle);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hookHandle, int code, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookInfo
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookInfo
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }
}
