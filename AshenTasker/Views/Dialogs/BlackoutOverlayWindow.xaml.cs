using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace AshenTasker.Views.Dialogs
{
    public partial class BlackoutOverlayWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExLayered = 0x00080000;
        private const int WmHotkey = 0x0312;
        private const int BlackoutPreviousMonitorHotkeyId = 0xB101;
        private const int BlackoutNextMonitorHotkeyId = 0xB102;
        private const uint ModNoRepeat = 0x4000;
        private const uint MonitorDefaultToNearest = 2;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private static readonly nint HwndTopmost = new(-1);

        private readonly nint _ownerHandle;
        private readonly List<MonitorBounds> _monitors = [];
        private HwndSource? _source;
        private nint _handle;
        private int _coverageIndex;
        private MonitorBounds _originalMonitor;

        public BlackoutOverlayWindow(nint ownerHandle = 0)
        {
            _ownerHandle = ownerHandle;
            InitializeComponent();
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.PrimaryScreenWidth / 2;
            Top = SystemParameters.PrimaryScreenHeight / 2;
            Width = 1;
            Height = 1;

            SourceInitialized += BlackoutOverlayWindow_OnSourceInitialized;
            Closed += BlackoutOverlayWindow_OnClosed;
            InstructionPanel.SizeChanged += (_, _) => PositionInstructions();
        }

        private void BlackoutOverlayWindow_OnSourceInitialized(object? sender, EventArgs e)
        {
            _handle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(_handle);
            _source?.AddHook(WndProc);
            LoadMonitorBounds();
            ApplyCoverageIndex(_coverageIndex);

            int exStyle = GetWindowLong(_handle, GwlExStyle);
            SetWindowLong(_handle, GwlExStyle, exStyle | WsExTransparent | WsExLayered);

            RegisterHotKey(
                _handle,
                BlackoutPreviousMonitorHotkeyId,
                ModNoRepeat,
                (uint)KeyInterop.VirtualKeyFromKey(Key.Left));
            RegisterHotKey(
                _handle,
                BlackoutNextMonitorHotkeyId,
                ModNoRepeat,
                (uint)KeyInterop.VirtualKeyFromKey(Key.Right));
        }

        private void BlackoutOverlayWindow_OnClosed(object? sender, EventArgs e)
        {
            if (_handle != 0)
            {
                UnregisterHotKey(_handle, BlackoutPreviousMonitorHotkeyId);
                UnregisterHotKey(_handle, BlackoutNextMonitorHotkeyId);
            }

            _source?.RemoveHook(WndProc);
            _source = null;
        }

        private void LoadMonitorBounds()
        {
            _monitors.Clear();
            EnumDisplayMonitors(0, 0, MonitorEnumProc, 0);
            _monitors.Sort((left, right) =>
            {
                int topCompare = left.DeviceBounds.Top.CompareTo(right.DeviceBounds.Top);
                return topCompare != 0
                    ? topCompare
                    : left.DeviceBounds.Left.CompareTo(right.DeviceBounds.Left);
            });

            if (_monitors.Count == 0)
            {
                _monitors.Add(new MonitorBounds(
                    new NativeRect(
                        (int)SystemParameters.VirtualScreenLeft,
                        (int)SystemParameters.VirtualScreenTop,
                        (int)(SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth),
                        (int)(SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight))));
            }

            nint ownerOrSelf = _ownerHandle != 0 ? _ownerHandle : _handle;
            nint ownerMonitorHandle = MonitorFromWindow(ownerOrSelf, MonitorDefaultToNearest);
            _coverageIndex = Math.Max(0, _monitors.FindIndex(monitor => monitor.Handle == ownerMonitorHandle));
            _originalMonitor = _monitors[_coverageIndex];
        }

        private bool MonitorEnumProc(nint monitorHandle, nint hdcMonitor, ref NativeRect lprcMonitor, nint data)
        {
            MonitorInfo monitorInfo = new();
            monitorInfo.Size = Marshal.SizeOf<MonitorInfo>();

            if (GetMonitorInfo(monitorHandle, ref monitorInfo))
            {
                _monitors.Add(new MonitorBounds(monitorInfo.Monitor, monitorHandle));
            }

            return true;
        }

        private void ApplyCoverageIndex(int coverageIndex)
        {
            MonitorBounds bounds = coverageIndex >= _monitors.Count
                ? GetAllMonitorBounds()
                : _monitors[coverageIndex];
            ApplyBounds(bounds);
            Dispatcher.BeginInvoke(new Action(PositionInstructions));
        }

        private void MoveCoverage(int direction)
        {
            int optionCount = _monitors.Count + 1;
            _coverageIndex = (_coverageIndex + direction + optionCount) % optionCount;
            ApplyCoverageIndex(_coverageIndex);
        }

        private MonitorBounds GetAllMonitorBounds()
        {
            int left = _monitors.Min(monitor => monitor.DeviceBounds.Left);
            int top = _monitors.Min(monitor => monitor.DeviceBounds.Top);
            int right = _monitors.Max(monitor => monitor.DeviceBounds.Right);
            int bottom = _monitors.Max(monitor => monitor.DeviceBounds.Bottom);
            return new MonitorBounds(new NativeRect(left, top, right, bottom));
        }

        private void ApplyBounds(MonitorBounds bounds)
        {
            Rect dipBounds = ToDipRect(bounds.DeviceBounds);
            Left = dipBounds.Left;
            Top = dipBounds.Top;
            Width = dipBounds.Width;
            Height = dipBounds.Height;
            ForceTopmostBounds(bounds.DeviceBounds);
        }

        private void ForceTopmostBounds(NativeRect deviceBounds)
        {
            if (_handle == 0)
            {
                return;
            }

            int exStyle = GetWindowLong(_handle, GwlExStyle);
            SetWindowLong(_handle, GwlExStyle, exStyle | WsExTransparent | WsExLayered);
            SetWindowPos(
                _handle,
                HwndTopmost,
                deviceBounds.Left,
                deviceBounds.Top,
                Math.Max(1, deviceBounds.Right - deviceBounds.Left),
                Math.Max(1, deviceBounds.Bottom - deviceBounds.Top),
                SwpNoActivate | SwpShowWindow);
        }

        private void PositionInstructions()
        {
            if (_source is null)
            {
                return;
            }

            Rect originalBounds = ToDipRect(_originalMonitor.DeviceBounds);
            double centerX = originalBounds.Left + originalBounds.Width / 2 - Left;
            double centerY = originalBounds.Top + originalBounds.Height / 2 - Top;

            Canvas.SetLeft(InstructionPanel, centerX - InstructionPanel.ActualWidth / 2);
            Canvas.SetTop(InstructionPanel, centerY - InstructionPanel.ActualHeight / 2);
        }

        private Rect ToDipRect(NativeRect deviceRect)
        {
            Matrix transform = _source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            Point topLeft = transform.Transform(new Point(deviceRect.Left, deviceRect.Top));
            Point bottomRight = transform.Transform(new Point(deviceRect.Right, deviceRect.Bottom));
            return new Rect(topLeft, bottomRight);
        }

        private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
        {
            if (msg != WmHotkey)
            {
                return 0;
            }

            int hotkeyId = wParam.ToInt32();
            if (hotkeyId == BlackoutPreviousMonitorHotkeyId)
            {
                MoveCoverage(-1);
                handled = true;
            }
            else if (hotkeyId == BlackoutNextMonitorHotkeyId)
            {
                MoveCoverage(1);
                handled = true;
            }

            return 0;
        }

        private readonly record struct MonitorBounds(NativeRect DeviceBounds, nint Handle = 0);

        private delegate bool MonitorEnumDelegate(nint hMonitor, nint hdcMonitor, ref NativeRect lprcMonitor, nint dwData);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public NativeRect(int left, int top, int right, int bottom)
            {
                Left = left;
                Top = top;
                Right = right;
                Bottom = bottom;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect WorkArea;
            public uint Flags;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(nint hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(nint hWnd, int id);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumDelegate lpfnEnum, nint dwData);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfo lpmi);

        [DllImport("user32.dll")]
        private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            nint hWnd,
            nint hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint flags);
    }
}
