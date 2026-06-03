using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AshenTasker.Models.Windowing;

namespace AshenTasker.Services.Windowing;

public sealed class WindowEnumerationService
{
    #region Window Discovery

    public IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        List<WindowInfo> windows = [];

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || hwnd == GetShellWindow())
            {
                return true;
            }

            int length = GetWindowTextLength(hwnd);
            if (length == 0)
            {
                return true;
            }

            StringBuilder titleBuilder = new(length + 1);
            GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity);
            string title = titleBuilder.ToString().Trim();

            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out uint processId);

            string processName;
            try
            {
                processName = Process.GetProcessById((int)processId).ProcessName;
            }
            catch
            {
                processName = "Unknown";
            }

            if (!GetWindowRect(hwnd, out Rect rect))
            {
                return true;
            }

            windows.Add(new WindowInfo(
                hwnd,
                title,
                processName,
                (int)processId,
                rect.Left,
                rect.Top,
                rect.Right,
                rect.Bottom));

            return true;
        }, IntPtr.Zero);

        return windows
            .OrderBy(window => window.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    #endregion

    #region Window Placement

    public bool TryGetWindowBounds(WindowInfo window, out WindowBounds bounds)
    {
        if (!GetWindowRect(window.Handle, out Rect rect))
        {
            bounds = default;
            return false;
        }

        bounds = new WindowBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    public bool IsWindowAlive(WindowInfo window)
    {
        return IsWindow(window.Handle) && IsWindowVisible(window.Handle);
    }

    public bool TryGetFrameInsets(WindowInfo window, out WindowFrameInsets insets)
    {
        insets = default;

        if (!TryGetWindowBounds(window, out WindowBounds windowBounds)
            || !TryGetClientBounds(window, out WindowBounds clientBounds))
        {
            return false;
        }

        insets = new WindowFrameInsets(
            clientBounds.Left - windowBounds.Left,
            clientBounds.Top - windowBounds.Top,
            windowBounds.Right - clientBounds.Right,
            windowBounds.Bottom - clientBounds.Bottom);

        return insets.Left >= 0
               && insets.Top >= 0
               && insets.Right >= 0
               && insets.Bottom >= 0;
    }

    public bool TryGetMonitorWorkArea(WindowInfo window, out WindowBounds bounds)
    {
        nint monitor = MonitorFromWindow(window.Handle, MonitorDefaultToNearest);
        MonitorInfo monitorInfo = new()
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };

        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            bounds = default;
            return false;
        }

        bounds = new WindowBounds(
            monitorInfo.WorkArea.Left,
            monitorInfo.WorkArea.Top,
            monitorInfo.WorkArea.Right,
            monitorInfo.WorkArea.Bottom);

        return true;
    }

    public bool TryMoveWindow(WindowInfo window, int left, int top, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        // Restoring first lets normal maximized windows accept the new fitted bounds.
        ShowWindow(window.Handle, ShowWindowRestore);

        return SetWindowPos(
            window.Handle,
            nint.Zero,
            left,
            top,
            width,
            height,
            SetWindowPosNoZOrder | SetWindowPosNoActivate | SetWindowPosShowWindow);
    }

    public bool TryGetClientBounds(WindowInfo window, out WindowBounds bounds)
    {
        bounds = default;

        if (!GetClientRect(window.Handle, out Rect clientRect))
        {
            return false;
        }

        NativePoint topLeft = new()
        {
            X = clientRect.Left,
            Y = clientRect.Top
        };

        if (!ClientToScreen(window.Handle, ref topLeft))
        {
            return false;
        }

        bounds = new WindowBounds(
            topLeft.X,
            topLeft.Y,
            topLeft.X + clientRect.Right - clientRect.Left,
            topLeft.Y + clientRect.Bottom - clientRect.Top);

        return bounds.Width > 0 && bounds.Height > 0;
    }

    public bool TryMoveWindowClientToBounds(WindowInfo window, WindowBounds clientBounds)
    {
        if (clientBounds.Width <= 0 || clientBounds.Height <= 0)
        {
            return false;
        }

        ShowWindow(window.Handle, ShowWindowRestore);

        if (!GetWindowRect(window.Handle, out Rect windowRect)
            || !TryGetClientBounds(window, out WindowBounds currentClientBounds))
        {
            return false;
        }

        int leftInset = currentClientBounds.Left - windowRect.Left;
        int topInset = currentClientBounds.Top - windowRect.Top;
        int rightInset = windowRect.Right - currentClientBounds.Right;
        int bottomInset = windowRect.Bottom - currentClientBounds.Bottom;

        return TryMoveWindow(
            window,
            clientBounds.Left - leftInset,
            clientBounds.Top - topInset,
            clientBounds.Width + leftInset + rightInset,
            clientBounds.Height + topInset + bottomInset);
    }

    public bool TryGetCursorClientPosition(WindowInfo window, out WindowClientPoint position)
    {
        position = default;

        if (!GetCursorPos(out NativePoint cursor)
            || !TryGetClientBounds(window, out WindowBounds clientBounds)
            || clientBounds.Width <= 0
            || clientBounds.Height <= 0)
        {
            return false;
        }

        if (cursor.X < clientBounds.Left
            || cursor.X >= clientBounds.Right
            || cursor.Y < clientBounds.Top
            || cursor.Y >= clientBounds.Bottom)
        {
            return false;
        }

        int clientX = cursor.X - clientBounds.Left;
        int clientY = cursor.Y - clientBounds.Top;

        position = new WindowClientPoint(
            clientX,
            clientY,
            clientX / (double)clientBounds.Width,
            clientY / (double)clientBounds.Height);

        return true;
    }

    public bool TryPlaceWindowBehind(WindowInfo window, nint foregroundWindowHandle)
    {
        if (foregroundWindowHandle == nint.Zero)
        {
            return false;
        }

        ShowWindow(window.Handle, ShowWindowRestore);

        return SetWindowPos(
            window.Handle,
            foregroundWindowHandle,
            0,
            0,
            0,
            0,
            SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosNoActivate | SetWindowPosShowWindow);
    }

    #endregion

    #region Native Methods

    private const uint MonitorDefaultToNearest = 2;
    private const int ShowWindowRestore = 9;
    private const uint SetWindowPosNoMove = 0x0002;
    private const uint SetWindowPosNoSize = 0x0001;
    private const uint SetWindowPosNoZOrder = 0x0004;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const uint SetWindowPosShowWindow = 0x0040;

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint hwndInsertAfter, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Rect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    #endregion
}
