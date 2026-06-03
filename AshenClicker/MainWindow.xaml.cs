using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace AshenClicker;

public partial class MainWindow : Window
{
    private const uint InputMouse = 0;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;
    private const uint MouseEventMiddleDown = 0x0020;
    private const uint MouseEventMiddleUp = 0x0040;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const int HotkeyId = 0xAC10;
    private const int WmHotkey = 0x0312;
    private const int UiUpdateMilliseconds = 250;
    private const int WindowCornerRadiusPixels = 16;
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const string VersionPrefix = "v";
    private const string VersionNumber = "0.1";
    private const string VersionChannel = "ALPHA";

    private HwndSource? _source;
    private CancellationTokenSource? _clickerCancellation;
    private readonly LowLevelKeyboardProc _keyboardProc;
    private nint _keyboardHook;
    private bool _isRunning;
    private bool _isCapturingHotkey;
    private bool _isCapturingTargetKey;
    private bool _isHotkeyPressed;
    private int _clickCount = 10;
    private string _hotkey = "F8";
    private string _targetKey = "Space";

    public MainWindow()
    {
        _keyboardProc = KeyboardHookCallback;
        InitializeComponent();
        VersionText.Text = $"{VersionPrefix}{VersionNumber} {VersionChannel}";
        SetStatus("idle", false);
        SourceInitialized += MainWindow_OnSourceInitialized;
        Closed += MainWindow_OnClosed;
        PreviewKeyDown += MainWindow_OnPreviewKeyDown;
        PreviewKeyUp += MainWindow_OnPreviewKeyUp;
        PreviewMouseDown += MainWindow_OnPreviewMouseDown;
        PreviewMouseUp += MainWindow_OnPreviewMouseUp;
        SizeChanged += MainWindow_OnSizeChanged;
        StateChanged += MainWindow_OnStateChanged;
    }

    private void MainWindow_OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WindowMessageHook);
        InstallKeyboardHook();
        RegisterHotkey();
        UpdateWindowRegion();
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        StopClicker("status: idle");
        UnregisterHotkey();
        UninstallKeyboardHook();
        _source?.RemoveHook(WindowMessageHook);
        _source = null;
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            if (_keyboardHook == 0)
            {
                if (PressRadioButton.IsChecked == true)
                {
                    StartClicker();
                }
                else
                {
                    ToggleClicker();
                }
            }

            handled = true;
        }

        return 0;
    }

    private void MainWindow_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateWindowRegion();
    }

    private void MainWindow_OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }
    }

    private void UpdateWindowRegion()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0)
        {
            return;
        }

        PresentationSource? source = PresentationSource.FromVisual(this);
        Matrix transform = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        Point size = transform.Transform(new Point(ActualWidth, ActualHeight));

        nint region = CreateRoundRectRgn(
            0,
            0,
            Math.Max(1, (int)Math.Ceiling(size.X)),
            Math.Max(1, (int)Math.Ceiling(size.Y)),
            WindowCornerRadiusPixels,
            WindowCornerRadiusPixels);

        if (SetWindowRgn(hwnd, region, true) == 0)
        {
            DeleteObject(region);
        }
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            return;
        }

        DragMove();
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetStatus("settings soon", false);
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void HotkeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        _isCapturingHotkey = true;
        _isCapturingTargetKey = false;
        HotkeyButton.Content = "...";
        HotkeyButton.Focus();
        SetStatus("press hotkey", false);
    }

    private void TargetKeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        _isCapturingTargetKey = true;
        _isCapturingHotkey = false;
        KeyTargetRadioButton.IsChecked = true;
        TargetKeyButton.Content = "...";
        TargetKeyButton.Focus();
        SetStatus("press target key", false);
    }

    private void StopButton_OnClick(object sender, RoutedEventArgs e)
    {
        _isHotkeyPressed = false;
        StopClicker("stopped");
    }

    private void MainWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        string keyName = GetKeyName(e.Key == Key.System ? e.SystemKey : e.Key);

        if (_isCapturingHotkey)
        {
            SetHotkey(keyName);
            e.Handled = true;
            return;
        }

        if (_isCapturingTargetKey)
        {
            SetTargetKey(keyName);
            e.Handled = true;
            return;
        }

        e.Handled = !IsMouseButtonHotkey(_hotkey)
            && string.Equals(_hotkey, keyName, StringComparison.OrdinalIgnoreCase);
    }

    private void MainWindow_OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        string keyName = GetKeyName(e.Key == Key.System ? e.SystemKey : e.Key);
        e.Handled = !IsMouseButtonHotkey(_hotkey)
            && string.Equals(_hotkey, keyName, StringComparison.OrdinalIgnoreCase);
    }

    private void MainWindow_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isCapturingHotkey)
        {
            SetStatus("mouse triggers disabled", false);
            HotkeyButton.Content = _hotkey;
            _isCapturingHotkey = false;
            e.Handled = true;
            return;
        }

        if (_isCapturingTargetKey)
        {
            SetStatus("choose a keyboard key", false);
            TargetKeyButton.Content = _targetKey;
            _isCapturingTargetKey = false;
            e.Handled = true;
        }
    }

    private void MainWindow_OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
    }

    private void SetHotkey(string hotkey)
    {
        _hotkey = hotkey;
        _isHotkeyPressed = false;
        _isCapturingHotkey = false;
        HotkeyButton.Content = hotkey;
        RegisterHotkey();
        SetStatus($"hotkey {hotkey}", false);
    }

    private void SetTargetKey(string hotkey)
    {
        _targetKey = hotkey;
        _isCapturingTargetKey = false;
        TargetKeyButton.Content = hotkey;
        KeyTargetRadioButton.IsChecked = true;
        SetStatus($"target key {hotkey}", false);
    }

    private void ToggleClicker()
    {
        if (_isRunning)
        {
            StopClicker("status: idle");
            return;
        }

        StartClicker();
    }

    private void StartClicker()
    {
        if (!double.TryParse(ClicksPerSecondTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double clicksPerSecond)
            || clicksPerSecond <= 0)
        {
            SetStatus("invalid clicks/s", false);
            return;
        }

        if (!TryCreateClickAction(out ClickAction clickAction))
        {
            SetStatus("invalid target key", false);
            return;
        }

        int stopAt = GetStopAt();
        TimeSpan interval = TimeSpan.FromSeconds(1 / clicksPerSecond);

        _clickerCancellation?.Cancel();
        _clickerCancellation = new CancellationTokenSource();
        _isRunning = true;
        SetStatus("running", true);
        _ = RunClickerLoopAsync(clickAction, interval, stopAt, _clickerCancellation.Token);
    }

    private async Task RunClickerLoopAsync(ClickAction clickAction, TimeSpan interval, int stopAt, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        long intervalTicks = Math.Max(1, (long)Math.Round(interval.TotalSeconds * Stopwatch.Frequency));
        long nextClickTicks = stopwatch.ElapsedTicks;
        long lastUiUpdateTicks = 0;
        int clickCount = _clickCount;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                long ticksUntilClick = nextClickTicks - stopwatch.ElapsedTicks;
                if (ticksUntilClick > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(ticksUntilClick / (double)Stopwatch.Frequency), cancellationToken);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (!SendClickAction(clickAction))
                {
                    Dispatcher.Invoke(() => StopClicker("status: input failed"));
                    break;
                }

                clickCount++;
                long elapsedTicks = stopwatch.ElapsedTicks;
                if (elapsedTicks - lastUiUpdateTicks >= Stopwatch.Frequency * UiUpdateMilliseconds / 1000)
                {
                    lastUiUpdateTicks = elapsedTicks;
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _clickCount = clickCount;
                        ClickCountTextBox.Text = _clickCount.ToString(CultureInfo.CurrentCulture);
                    }));
                }

                if (stopAt > 0 && clickCount >= stopAt)
                {
                    Dispatcher.Invoke(() =>
                    {
                        _clickCount = clickCount;
                        ClickCountTextBox.Text = _clickCount.ToString(CultureInfo.CurrentCulture);
                        StopClicker("status: idle");
                    });
                    break;
                }

                nextClickTicks += intervalTicks;
                if (nextClickTicks < elapsedTicks)
                {
                    nextClickTicks = elapsedTicks + intervalTicks;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                _clickCount = clickCount;
                ClickCountTextBox.Text = _clickCount.ToString(CultureInfo.CurrentCulture);
            }));
        }
    }

    private void StopClicker(string status)
    {
        _isRunning = false;
        _clickerCancellation?.Cancel();
        _clickerCancellation = null;
        SetStatus(status, false);
    }

    private void SetStatus(string status, bool isRunning)
    {
        string normalizedStatus = status.StartsWith("status:", StringComparison.OrdinalIgnoreCase)
            ? status[7..].Trim()
            : status.Trim();

        StatusText.Text = $"Status: {normalizedStatus}";
        StatusDot.Fill = new SolidColorBrush(isRunning
            ? Color.FromRgb(88, 158, 103)
            : Color.FromRgb(154, 58, 58));
    }

    private nint KeyboardHookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0
            && !_isCapturingHotkey
            && !_isCapturingTargetKey
            && TryGetHotkeyVirtualKey(out int hotkeyVirtualKey))
        {
            int message = wParam.ToInt32();
            int virtualKey = Marshal.ReadInt32(lParam);
            if (virtualKey == hotkeyVirtualKey)
            {
                if (message is WmKeyDown or WmSysKeyDown)
                {
                    if (!_isHotkeyPressed)
                    {
                        _isHotkeyPressed = true;
                        if (PressRadioButton.IsChecked == true)
                        {
                            _ = Dispatcher.BeginInvoke(new Action(StartClicker));
                        }
                        else
                        {
                            _ = Dispatcher.BeginInvoke(new Action(ToggleClicker));
                        }
                    }
                }
                else if (message is WmKeyUp or WmSysKeyUp)
                {
                    if (_isHotkeyPressed)
                    {
                        _isHotkeyPressed = false;
                        if (PressRadioButton.IsChecked == true)
                        {
                            _ = Dispatcher.BeginInvoke(new Action(() => StopClicker("status: idle")));
                        }
                    }
                }
            }
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private int GetStopAt()
    {
        return int.TryParse(StopAtTextBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int stopAt)
            ? Math.Max(0, stopAt)
            : 0;
    }

    private void StopAtTextBox_OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (string.Equals(StopAtTextBox.Text, "never", StringComparison.OrdinalIgnoreCase))
        {
            StopAtTextBox.Text = "0";
            StopAtTextBox.SelectAll();
        }
    }

    private void StopAtTextBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (GetStopAt() == 0)
        {
            StopAtTextBox.Text = "never";
        }
    }

    private bool TryCreateClickAction(out ClickAction clickAction)
    {
        if (MiddleRadioButton.IsChecked == true)
        {
            clickAction = ClickAction.Mouse(MouseEventMiddleDown, MouseEventMiddleUp);
            return true;
        }

        if (RightRadioButton.IsChecked == true)
        {
            clickAction = ClickAction.Mouse(MouseEventRightDown, MouseEventRightUp);
            return true;
        }

        if (KeyTargetRadioButton.IsChecked == true)
        {
            if (Enum.TryParse(_targetKey, true, out Key key))
            {
                int virtualKey = KeyInterop.VirtualKeyFromKey(key);
                if (virtualKey > 0)
                {
                    clickAction = ClickAction.Keyboard((ushort)virtualKey);
                    return true;
                }
            }

            clickAction = default;
            return false;
        }

        clickAction = ClickAction.Mouse(MouseEventLeftDown, MouseEventLeftUp);
        return true;
    }

    private static bool SendClickAction(ClickAction clickAction)
    {
        NativeInput[] inputs = clickAction.Kind == ClickActionKind.Keyboard
            ?
            [
                new() { Type = InputKeyboard, Data = new NativeInputUnion { Keyboard = new NativeKeyboardInput { VirtualKey = clickAction.VirtualKey } } },
                new() { Type = InputKeyboard, Data = new NativeInputUnion { Keyboard = new NativeKeyboardInput { VirtualKey = clickAction.VirtualKey, Flags = KeyEventKeyUp } } }
            ]
            :
            [
                new() { Type = InputMouse, Data = new NativeInputUnion { Mouse = new NativeMouseInput { Flags = clickAction.DownFlag } } },
                new() { Type = InputMouse, Data = new NativeInputUnion { Mouse = new NativeMouseInput { Flags = clickAction.UpFlag } } }
            ];

        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) == inputs.Length;
    }

    private void RegisterHotkey()
    {
        if (_source is null)
        {
            return;
        }

        UnregisterHotkey();

        if (IsMouseButtonHotkey(_hotkey) || !Enum.TryParse(_hotkey, true, out Key key))
        {
            return;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey > 0)
        {
            RegisterHotKey(_source.Handle, HotkeyId, 0, (uint)virtualKey);
        }
    }

    private void UnregisterHotkey()
    {
        if (_source is not null)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
        }
    }

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != 0)
        {
            return;
        }

        using Process currentProcess = Process.GetCurrentProcess();
        using ProcessModule? currentModule = currentProcess.MainModule;
        nint moduleHandle = currentModule is null ? 0 : GetModuleHandle(currentModule.ModuleName);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, moduleHandle, 0);
    }

    private void UninstallKeyboardHook()
    {
        if (_keyboardHook == 0)
        {
            return;
        }

        UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = 0;
    }

    private bool TryGetHotkeyVirtualKey(out int virtualKey)
    {
        virtualKey = 0;
        if (IsMouseButtonHotkey(_hotkey) || !Enum.TryParse(_hotkey, true, out Key key))
        {
            return false;
        }

        virtualKey = KeyInterop.VirtualKeyFromKey(key);
        return virtualKey > 0;
    }

    private static string GetKeyName(Key key)
    {
        return key == Key.None ? "#" : key.ToString();
    }

    private static bool IsMouseButtonHotkey(string hotkey)
    {
        return string.Equals(hotkey, nameof(MouseButton.Left), StringComparison.OrdinalIgnoreCase)
               || string.Equals(hotkey, nameof(MouseButton.Middle), StringComparison.OrdinalIgnoreCase)
               || string.Equals(hotkey, nameof(MouseButton.Right), StringComparison.OrdinalIgnoreCase)
               || string.Equals(hotkey, nameof(MouseButton.XButton1), StringComparison.OrdinalIgnoreCase)
               || string.Equals(hotkey, nameof(MouseButton.XButton2), StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct ClickAction(ClickActionKind Kind, uint DownFlag, uint UpFlag, ushort VirtualKey)
    {
        public static ClickAction Mouse(uint downFlag, uint upFlag)
        {
            return new ClickAction(ClickActionKind.Mouse, downFlag, upFlag, 0);
        }

        public static ClickAction Keyboard(ushort virtualKey)
        {
            return new ClickAction(ClickActionKind.Keyboard, 0, 0, virtualKey);
        }
    }

    private enum ClickActionKind
    {
        Mouse,
        Keyboard
    }

    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Data;
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
        public int DeltaX;
        public int DeltaY;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hookId, LowLevelKeyboardProc callback, nint instance, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
}
