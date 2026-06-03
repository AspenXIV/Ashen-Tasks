using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AshenTasker.Configuration;
using AshenTasker.Models.Macros;
using AshenTasker.Models.Storage;
using AshenTasker.Models.Windowing;
using AshenTasker.Services.Input;
using AshenTasker.Services.Storage;
using AshenTasker.Services.Windowing;
using AshenTasker.Views.Dialogs;
using Wpf.Ui.Controls;
using WpfButton = System.Windows.Controls.Button;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfShape = System.Windows.Shapes.Shape;
using WpfTreeViewItem = System.Windows.Controls.TreeViewItem;

namespace AshenTasker
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : FluentWindow
    {
        #region Fields And Properties

        private readonly MacroLibraryService _macroLibraryService = new();
        private readonly WindowEnumerationService _windowEnumerationService = new();
        private readonly MacroRecorderService _macroRecorderService;
        private readonly MacroPlaybackService _macroPlaybackService;
        private readonly DispatcherTimer _clockTimer = new();
        private readonly DispatcherTimer _recordingTimer = new();
        private readonly DispatcherTimer _targetPulseTimer = new();
        private readonly DispatcherTimer _targetSyncTimer = new();
        private readonly Random _random = new();
        private readonly List<GameWorkspaceTab> _gameTabs = [];
        private readonly List<MacroEditorTab> _macroEditorTabs = [];
        private readonly List<Border> _clientLayoutFrames = [];
        private readonly List<Grid> _multiEditorHosts = [];
        private readonly Dictionary<GameWorkspaceTab, FrameworkElement> _gamePaneTargets = [];
        private readonly List<Grid> _gamePaneHosts = [];
        private readonly List<Grid> _emptyPaneHosts = [];
        private BlackoutOverlayWindow? _blackoutOverlayWindow;

        private const int TargetTitleBarFitAdjustmentPixels = 2;
        private const int TargetFrameBleedPixels = 2;
        private const int TitleBarHeightPixels = 34;
        private const uint InputMouse = 0;
        private const uint MouseEventLeftDown = 0x0002;
        private const uint MouseEventLeftUp = 0x0004;
        private const uint MouseEventRightDown = 0x0008;
        private const uint MouseEventRightUp = 0x0010;
        private const uint MouseEventMiddleDown = 0x0020;
        private const uint MouseEventMiddleUp = 0x0040;
        private const int AutoclickerHotkeyId = 0xA800;
        private const int BlackoutToggleHotkeyId = 0xB100;
        private const int AutoclickerFocusWaitMilliseconds = 120;
        private const int AutoclickerPostFocusDelayMilliseconds = 18;
        private const int AutoclickerPostMoveDelayMilliseconds = 4;
        private const int AutoclickerBroadcastTargetDelayMilliseconds = 12;
        private const int AutoclickerUiUpdateMilliseconds = 250;
        private const int CursorTextUpdateMilliseconds = 100;
        private const int AntiAfkHotkeyId = 0xA801;
        private const int AntiAfkInitialDelayMilliseconds = 2000;
        private const int AntiAfkMissingCursorRetryMilliseconds = 5000;
        private const double AntiAfkClicksPerSecond = 0.002;
        private static readonly TimeSpan AntiAfkCycleDelay = TimeSpan.FromSeconds(1 / AntiAfkClicksPerSecond);
        private const int WmHotkey = 0x0312;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModNoRepeat = 0x4000;
        private const uint MonitorDefaultToNearest = 2;

        private WindowInfo? _selectedTargetWindow;
        private GameWorkspaceTab? _activeGameTab;
        private string? _currentMacroPath;
        private MacroEditorTab? _activeMacroEditorTab;
        private HwndSource? _mainHwndSource;
        private DateTimeOffset? _recordingStartedAt;
        private DateTimeOffset? _targetSyncUntil;
        private long _lastCursorTextUpdateTicks;
        private string _lastCursorText = string.Empty;
        private TargetResolutionMode _targetResolutionMode = TargetResolutionMode.Follow;
        private ClientLayoutMode _clientLayoutMode = ClientLayoutMode.OneByOne;
        private double _playbackSpeed = 1;
        private bool _isRecording;
        private bool _isPlaying;
        private bool _autoFollowWindow = true;
        private bool _isUpdatingEditorText;
        private bool _isTargetWorkspaceActive = true;
        private bool _isTargetSyncPaused;
        private bool _isDraggingTitleBar;
        private bool _isAutoclickerRunning;
        private bool _isAntiAfkRunning;
        private bool _isCapturingAutoclickerHotkey;
        private bool _isCapturingAntiAfkHotkey;
        private bool _isLoadingAutoclickerSettings;
        private int _autoclickerClickCount = 10;
        private string? _autoclickerHotkey = "F8";
        private string? _antiAfkHotkey = "F7";
        private CancellationTokenSource? _autoclickerCancellation;
        private CancellationTokenSource? _antiAfkCancellation;
        private CancellationTokenSource? _playbackCancellation;

        #endregion

        #region Construction

        public MainWindow()
        {
            InitializeComponent();
            _macroRecorderService = new MacroRecorderService(_windowEnumerationService);
            _macroPlaybackService = new MacroPlaybackService(_windowEnumerationService);

            SourceInitialized += MainWindow_OnSourceInitialized;
            Loaded += MainWindow_OnLoaded;
            Closed += MainWindow_OnClosed;
            Activated += MainWindow_OnActivated;
            LocationChanged += MainWindow_OnLocationChanged;
            SizeChanged += MainWindow_OnSizeChanged;
            StateChanged += MainWindow_OnStateChanged;
            TargetViewport.SizeChanged += TargetViewport_OnSizeChanged;
            PreviewKeyDown += MainWindow_OnPreviewKeyDown;
            PreviewKeyUp += MainWindow_OnPreviewKeyUp;
            PreviewMouseDown += MainWindow_OnPreviewMouseDown;
            PreviewMouseUp += MainWindow_OnPreviewMouseUp;
            ApplyAutoclickerSettings();
            HookAutoclickerSettingsEvents();

            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += ClockTimer_OnTick;

            _recordingTimer.Interval = TimeSpan.FromMilliseconds(250);
            _recordingTimer.Tick += RecordingTimer_OnTick;

            _targetPulseTimer.Interval = TimeSpan.FromMilliseconds(280);
            _targetPulseTimer.Tick += TargetPulseTimer_OnTick;

            _targetSyncTimer.Interval = TimeSpan.FromMilliseconds(16);
            _targetSyncTimer.Tick += TargetSyncTimer_OnTick;
        }

        private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
        {
            EnsureDefaultGameTab();
            SyncActiveGameTabToWindowState();
            RefreshMacroTree();
            RefreshWorkspaceTabs();
            UpdatePlaybackSpeedText();
            UpdateRecordingUi();
            UpdatePlaybackUi();
            UpdateTargetIndicator();
            UpdateWindowHoleRegion();

            _clockTimer.Start();
            CompositionTarget.Rendering += CursorRendering_OnRendering;
            ClockTimer_OnTick(this, EventArgs.Empty);
        }

        private void MainWindow_OnClosed(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= CursorRendering_OnRendering;
            _clockTimer.Stop();
            _recordingTimer.Stop();
            _targetPulseTimer.Stop();
            _targetSyncTimer.Stop();
            _autoclickerCancellation?.Cancel();
            _antiAfkCancellation?.Cancel();
            _playbackCancellation?.Cancel();
            _macroRecorderService.Dispose();
            UnregisterAutoclickerHotkey();
            UnregisterAntiAfkHotkey();
            UnregisterBlackoutHotkey();
        }

        #endregion

        #region Left Rail Modes

        private void AutoclickerHotkeyButton_OnClick(object sender, RoutedEventArgs e)
        {
            _isCapturingAutoclickerHotkey = true;
            AutoclickerHotkeyButton.Content = "...";
            AutoclickerHotkeyButton.Focus();
            SetStatus("Press a hotkey or mouse button for the autoclicker.");
        }

        private void AutoclickerRailTabButton_OnClick(object sender, RoutedEventArgs e)
        {
            SetLeftToolTab(isAntiAfkActive: false);
        }

        private void AntiAfkRailTabButton_OnClick(object sender, RoutedEventArgs e)
        {
            SetLeftToolTab(isAntiAfkActive: true);
        }

        private void SetLeftToolTab(bool isAntiAfkActive)
        {
            AutoclickerPanel.Visibility = isAntiAfkActive ? Visibility.Collapsed : Visibility.Visible;
            AntiAfkPanel.Visibility = isAntiAfkActive ? Visibility.Visible : Visibility.Collapsed;
            AutoclickerRailTabButton.Style = (Style)FindResource(isAntiAfkActive ? "RailModeTabButtonStyle" : "RailModeTabActiveButtonStyle");
            AntiAfkRailTabButton.Style = (Style)FindResource(isAntiAfkActive ? "RailModeTabActiveButtonStyle" : "RailModeTabButtonStyle");
        }

        private void AntiAfkToggleButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (_isAntiAfkRunning)
            {
                StopAntiAfk("Anti-AFK stopped.");
                return;
            }

            StartAntiAfk();
        }

        private void AntiAfkHotkeyButton_OnClick(object sender, RoutedEventArgs e)
        {
            _isCapturingAntiAfkHotkey = true;
            AntiAfkHotkeyButton.Content = "...";
            AntiAfkHotkeyButton.Focus();
            SetStatus("Press a hotkey or mouse button for Anti-AFK.");
        }

        private void MainWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            string keyName = GetKeyName(e.Key == Key.System ? e.SystemKey : e.Key);

            if (_isCapturingAutoclickerHotkey)
            {
                SetAutoclickerHotkey(keyName);
                e.Handled = true;
                return;
            }

            if (_isCapturingAntiAfkHotkey)
            {
                SetAntiAfkHotkey(keyName);
                e.Handled = true;
                return;
            }

            if (HandleConfiguredHotkey(e, keyName))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_autoclickerHotkey)
                && string.Equals(_autoclickerHotkey, keyName, StringComparison.OrdinalIgnoreCase))
            {
                HandleAutoclickerHotkeyPressed(e.IsRepeat);
                e.Handled = true;
            }

            if (!string.IsNullOrWhiteSpace(_antiAfkHotkey)
                && string.Equals(_antiAfkHotkey, keyName, StringComparison.OrdinalIgnoreCase)
                && !e.IsRepeat)
            {
                ToggleAntiAfk();
                e.Handled = true;
            }
        }

        private void MainWindow_OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            string keyName = GetKeyName(e.Key == Key.System ? e.SystemKey : e.Key);

            if (!string.IsNullOrWhiteSpace(_autoclickerHotkey)
                && string.Equals(_autoclickerHotkey, keyName, StringComparison.OrdinalIgnoreCase)
                && AutoclickerPressRadioButton.IsChecked == true)
            {
                StopAutoclicker("Autoclicker stopped.");
                e.Handled = true;
            }
        }

        private void MainWindow_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            string buttonName = e.ChangedButton.ToString();

            if (_isCapturingAutoclickerHotkey)
            {
                SetAutoclickerHotkey(buttonName);
                e.Handled = true;
                return;
            }

            if (_isCapturingAntiAfkHotkey)
            {
                SetAntiAfkHotkey(buttonName);
                e.Handled = true;
                return;
            }

            if (!string.IsNullOrWhiteSpace(_autoclickerHotkey)
                && string.Equals(_autoclickerHotkey, buttonName, StringComparison.OrdinalIgnoreCase))
            {
                HandleAutoclickerHotkeyPressed(false);
                e.Handled = true;
            }
        }

        private void MainWindow_OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            string buttonName = e.ChangedButton.ToString();

            if (!string.IsNullOrWhiteSpace(_autoclickerHotkey)
                && string.Equals(_autoclickerHotkey, buttonName, StringComparison.OrdinalIgnoreCase)
                && AutoclickerPressRadioButton.IsChecked == true)
            {
                StopAutoclicker("Autoclicker stopped.");
                e.Handled = true;
            }
        }

        private static string GetKeyName(Key key)
        {
            return key == Key.None ? "#" : key.ToString();
        }

        private bool HandleConfiguredHotkey(KeyEventArgs e, string keyName)
        {
            if (e.IsRepeat || IsModifierKey(e.Key))
            {
                return false;
            }

            if (IsHotkeyMatch(AppSettings.RecordHotkey, keyName))
            {
                StartRecording();
                e.Handled = true;
                return true;
            }

            if (IsHotkeyMatch(AppSettings.StopHotkey, keyName))
            {
                if (_isRecording)
                {
                    StopRecording();
                }
                else if (_isPlaying)
                {
                    StopPlayback();
                }

                e.Handled = true;
                return true;
            }

            if (IsHotkeyMatch(AppSettings.PlayHotkey, keyName))
            {
                PlayButton_OnClick(this, new RoutedEventArgs());
                e.Handled = true;
                return true;
            }

            return false;
        }

        private static bool IsHotkeyMatch(string configuredHotkey, string keyName)
        {
            if (string.IsNullOrWhiteSpace(configuredHotkey))
            {
                return false;
            }

            string[] parts = configuredHotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || !string.Equals(parts[^1], keyName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            ModifierKeys modifiers = Keyboard.Modifiers;
            bool needsCtrl = parts.Any(part => string.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase)
                                               || string.Equals(part, "Control", StringComparison.OrdinalIgnoreCase));
            bool needsShift = parts.Any(part => string.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase));
            bool needsAlt = parts.Any(part => string.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase));
            bool needsWin = parts.Any(part => string.Equals(part, "Win", StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(part, "Windows", StringComparison.OrdinalIgnoreCase));

            return modifiers.HasFlag(ModifierKeys.Control) == needsCtrl
                   && modifiers.HasFlag(ModifierKeys.Shift) == needsShift
                   && modifiers.HasFlag(ModifierKeys.Alt) == needsAlt
                   && modifiers.HasFlag(ModifierKeys.Windows) == needsWin;
        }

        private static bool IsModifierKey(Key key)
        {
            return key is Key.LeftCtrl
                or Key.RightCtrl
                or Key.LeftShift
                or Key.RightShift
                or Key.LeftAlt
                or Key.RightAlt
                or Key.LWin
                or Key.RWin
                or Key.System;
        }

        private void SetAutoclickerHotkey(string hotkey)
        {
            _autoclickerHotkey = hotkey;
            _isCapturingAutoclickerHotkey = false;
            AutoclickerHotkeyButton.Content = hotkey;
            SaveAutoclickerSettings();
            RegisterAutoclickerHotkey();
            SetStatus($"Autoclicker hotkey set to {hotkey}.");
        }

        private void ToggleAutoclicker()
        {
            if (_isAutoclickerRunning)
            {
                StopAutoclicker("Autoclicker stopped.");
                return;
            }

            StartAutoclicker();
        }

        private void ToggleAntiAfk()
        {
            if (_isAntiAfkRunning)
            {
                StopAntiAfk("Anti-AFK stopped.");
                return;
            }

            StartAntiAfk();
        }

        private void SetAntiAfkHotkey(string hotkey)
        {
            _antiAfkHotkey = hotkey;
            _isCapturingAntiAfkHotkey = false;
            AntiAfkHotkeyButton.Content = hotkey;
            RegisterAntiAfkHotkey();
            SetStatus($"Anti-AFK hotkey set to {hotkey}.");
        }

        private void HandleAutoclickerHotkeyPressed(bool isRepeat)
        {
            if (AutoclickerPressRadioButton.IsChecked == true)
            {
                if (!_isAutoclickerRunning && !isRepeat)
                {
                    StartAutoclicker();
                }

                return;
            }

            if (!isRepeat)
            {
                ToggleAutoclicker();
            }
        }

        private void StartAutoclicker()
        {
            if (!double.TryParse(AutoclickerClicksPerSecondTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double clicksPerSecond)
                || clicksPerSecond <= 0)
            {
                SetStatus("Autoclicker clicks/s must be a positive number.");
                return;
            }

            if (_gameTabs.All(tab => tab.TargetWindow is null))
            {
                SetStatus("Autoclicker needs an attached target.");
                return;
            }

            GetAutoclickerMouseFlags(out uint downFlag, out uint upFlag);
            TimeSpan interval = TimeSpan.FromSeconds(1 / clicksPerSecond);
            int stopAt = GetAutoclickerStopAt();
            _autoclickerCancellation?.Cancel();
            _autoclickerCancellation = new CancellationTokenSource();
            _isAutoclickerRunning = true;
            AutoclickerStatusText.Text = "status: running";

            string clickTarget = AutoclickerLeftRadioButton.IsChecked == true
                ? "left"
                : AutoclickerMiddleRadioButton.IsChecked == true
                    ? "middle"
                    : "right";

            string mode = AutoclickerPressRadioButton.IsChecked == true ? "press" : "toggle";
            SetStatus($"Autoclicker {mode} started for {clickTarget} at {clicksPerSecond:0.###} clicks/s.");
            _ = RunAutoclickerLoopAsync(downFlag, upFlag, interval, stopAt, _autoclickerCancellation.Token);
        }

        private void StopAutoclickerButton_OnClick(object sender, RoutedEventArgs e)
        {
            StopAutoclicker("Autoclicker stopped.");
        }

        private async Task RunAutoclickerLoopAsync(
            uint downFlag,
            uint upFlag,
            TimeSpan interval,
            int stopAt,
            CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            long intervalTicks = Math.Max(1, (long)Math.Round(interval.TotalSeconds * Stopwatch.Frequency));
            long nextClickTicks = stopwatch.ElapsedTicks;
            long lastUiUpdateTicks = 0;
            int clickCount = _autoclickerClickCount;
            bool isPaused = false;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!TryGetAutoclickTargetUnderCursor(out WindowInfo? targetWindow))
                    {
                        if (!isPaused)
                        {
                            isPaused = true;
                            _ = Dispatcher.BeginInvoke(new Action(() => AutoclickerStatusText.Text = "status: paused"));
                        }

                        await Task.Delay(25, cancellationToken);
                        stopwatch.Restart();
                        nextClickTicks = stopwatch.ElapsedTicks;
                        continue;
                    }

                    if (isPaused)
                    {
                        isPaused = false;
                        _ = Dispatcher.BeginInvoke(new Action(() => AutoclickerStatusText.Text = "status: running"));
                        stopwatch.Restart();
                        nextClickTicks = stopwatch.ElapsedTicks;
                    }

                    long ticksUntilClick = nextClickTicks - stopwatch.ElapsedTicks;

                    if (ticksUntilClick > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(ticksUntilClick / (double)Stopwatch.Frequency), cancellationToken);
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    AutoclickSendResult sendResult = AutoclickerBroadcastCheckBox.IsChecked == true
                        ? await TrySendBroadcastAutoclickAsync(targetWindow!, downFlag, upFlag, cancellationToken)
                        : await TrySendAutoclickAsync(targetWindow!, downFlag, upFlag, cancellationToken);

                    if (!sendResult.WasSent)
                    {
                        await Dispatcher.InvokeAsync(() => StopAutoclicker(sendResult.ErrorMessage));
                        break;
                    }

                    clickCount++;
                    int displayedClickCount = clickCount;
                    long elapsedTicks = stopwatch.ElapsedTicks;
                    bool shouldUpdateUi = elapsedTicks - lastUiUpdateTicks >= Stopwatch.Frequency * AutoclickerUiUpdateMilliseconds / 1000;

                    if (shouldUpdateUi)
                    {
                        lastUiUpdateTicks = elapsedTicks;
                        _ = Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _autoclickerClickCount = displayedClickCount;
                            AutoclickerClickCountTextBox.Text = _autoclickerClickCount.ToString(CultureInfo.CurrentCulture);
                        }));
                    }

                    if (stopAt > 0 && clickCount >= stopAt)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            _autoclickerClickCount = clickCount;
                            AutoclickerClickCountTextBox.Text = _autoclickerClickCount.ToString(CultureInfo.CurrentCulture);
                            StopAutoclicker("Autoclicker stopped at target click count.");
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
                    _autoclickerClickCount = clickCount;
                    AutoclickerClickCountTextBox.Text = _autoclickerClickCount.ToString(CultureInfo.CurrentCulture);
                }));
            }
        }

        private async Task<AutoclickSendResult> TrySendAutoclickAsync(
            WindowInfo targetWindow,
            uint downFlag,
            uint upFlag,
            CancellationToken cancellationToken)
        {
            if (!_windowEnumerationService.TryGetCursorClientPosition(targetWindow, out _))
            {
                return AutoclickSendResult.Failed("Autoclicker click target was lost.");
            }

            if (!await FocusTargetWindowAsync(targetWindow, cancellationToken))
            {
                return AutoclickSendResult.Failed("Autoclicker could not focus the target window.");
            }

            NativeInput[] inputs =
            [
                new() { Type = InputMouse, Mouse = new NativeMouseInput { Flags = downFlag } },
                new() { Type = InputMouse, Mouse = new NativeMouseInput { Flags = upFlag } }
            ];

            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length)
            {
                return AutoclickSendResult.Failed("Autoclicker click input failed.");
            }

            return AutoclickSendResult.Sent();
        }

        private async Task<AutoclickSendResult> TrySendBroadcastAutoclickAsync(
            WindowInfo sourceWindow,
            uint downFlag,
            uint upFlag,
            CancellationToken cancellationToken)
        {
            if (!_windowEnumerationService.TryGetCursorClientPosition(sourceWindow, out WindowClientPoint sourcePosition))
            {
                return AutoclickSendResult.Failed("Autoclicker broadcast source was lost.");
            }

            List<WindowInfo> targets = GetAttachedTargetWindows();
            if (targets.Count == 0)
            {
                return AutoclickSendResult.Failed("Autoclicker needs an attached target.");
            }

            return await SendRelativeClickAcrossTargetsAsync(sourcePosition, targets, downFlag, upFlag, "Autoclicker broadcast input failed.", cancellationToken);
        }

        private async Task<AutoclickSendResult> SendRelativeClickAcrossTargetsAsync(
            WindowClientPoint sourcePosition,
            List<WindowInfo> targets,
            uint downFlag,
            uint upFlag,
            string failureMessage,
            CancellationToken cancellationToken)
        {
            bool sentAny = false;
            foreach (WindowInfo target in targets)
            {
                if (!_windowEnumerationService.TryGetClientBounds(target, out WindowBounds clientBounds))
                {
                    continue;
                }

                int x = clientBounds.Left + Math.Clamp((int)Math.Round(sourcePosition.NormalizedX * clientBounds.Width), 0, Math.Max(0, clientBounds.Width - 1));
                int y = clientBounds.Top + Math.Clamp((int)Math.Round(sourcePosition.NormalizedY * clientBounds.Height), 0, Math.Max(0, clientBounds.Height - 1));

                if (!await FocusTargetWindowAsync(target, cancellationToken))
                {
                    continue;
                }

                SetCursorPos(x, y);
                await Task.Delay(AutoclickerPostMoveDelayMilliseconds, cancellationToken);

                NativeInput[] inputs =
                [
                    new() { Type = InputMouse, Mouse = new NativeMouseInput { Flags = downFlag } },
                    new() { Type = InputMouse, Mouse = new NativeMouseInput { Flags = upFlag } }
                ];

                if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) == inputs.Length)
                {
                    sentAny = true;
                }

                if (targets.Count > 1)
                {
                    await Task.Delay(AutoclickerBroadcastTargetDelayMilliseconds, cancellationToken);
                }
            }

            if (!sentAny)
            {
                return AutoclickSendResult.Failed(failureMessage);
            }

            return AutoclickSendResult.Sent();
        }

        private void StartAntiAfk()
        {
            if (_gameTabs.All(tab => tab.TargetWindow is null))
            {
                SetStatus("Anti-AFK needs an attached target.");
                return;
            }

            _antiAfkCancellation?.Cancel();
            _antiAfkCancellation = new CancellationTokenSource();
            _isAntiAfkRunning = true;
            AntiAfkStatusText.Text = "status: waiting";
            AntiAfkToggleButton.Content = "DISABLE";
            SetStatus("Anti-AFK enabled.");
            _ = RunAntiAfkLoopAsync(_antiAfkCancellation.Token);
        }

        private void StopAntiAfk(string statusMessage)
        {
            _isAntiAfkRunning = false;
            _antiAfkCancellation?.Cancel();
            _antiAfkCancellation = null;
            AntiAfkStatusText.Text = "status: idle";
            AntiAfkToggleButton.Content = "ENABLE";
            SetStatus(statusMessage);
        }

        private async Task RunAntiAfkLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(AntiAfkInitialDelayMilliseconds, cancellationToken);

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!TryGetCursorTargetUnderCursor(out GameWorkspaceTab? sourceTab, out _)
                        || sourceTab?.TargetWindow is null)
                    {
                        _ = Dispatcher.BeginInvoke(new Action(() => AntiAfkStatusText.Text = "status: cursor outside target"));
                        await Task.Delay(AntiAfkMissingCursorRetryMilliseconds, cancellationToken);
                        continue;
                    }

                    List<WindowInfo> targets = GetAttachedTargetWindows();
                    if (targets.Count == 0)
                    {
                        await Dispatcher.InvokeAsync(() => StopAntiAfk("Anti-AFK stopped; no targets are attached."));
                        break;
                    }

                    _ = Dispatcher.BeginInvoke(new Action(() => AntiAfkStatusText.Text = "status: clicking"));
                    AutoclickSendResult result = await TrySendBroadcastAutoclickAsync(
                        sourceTab.TargetWindow,
                        MouseEventLeftDown,
                        MouseEventLeftUp,
                        cancellationToken);

                    if (!result.WasSent)
                    {
                        await Dispatcher.InvokeAsync(() => StopAntiAfk(result.ErrorMessage));
                        break;
                    }

                    DateTime nextRun = DateTime.Now.Add(AntiAfkCycleDelay);
                    _ = Dispatcher.BeginInvoke(new Action(() => AntiAfkStatusText.Text = $"status: next {nextRun:t}"));
                    await Task.Delay(AntiAfkCycleDelay, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task<bool> FocusTargetWindowAsync(WindowInfo targetWindow, CancellationToken cancellationToken)
        {
            if (GetForegroundWindow() == targetWindow.Handle)
            {
                return true;
            }

            if (!SetForegroundWindow(targetWindow.Handle))
            {
                await Task.Delay(AutoclickerPostFocusDelayMilliseconds, cancellationToken);
                return GetForegroundWindow() == targetWindow.Handle;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < AutoclickerFocusWaitMilliseconds)
            {
                if (GetForegroundWindow() == targetWindow.Handle)
                {
                    await Task.Delay(AutoclickerPostFocusDelayMilliseconds, cancellationToken);
                    return true;
                }

                await Task.Delay(4, cancellationToken);
            }

            if (GetForegroundWindow() == targetWindow.Handle)
            {
                await Task.Delay(AutoclickerPostFocusDelayMilliseconds, cancellationToken);
                return true;
            }

            return false;
        }

        private bool TryGetAutoclickTargetUnderCursor(out WindowInfo? targetWindow)
        {
            targetWindow = null;

            foreach (GameWorkspaceTab tab in _gameTabs)
            {
                if (tab.TargetWindow is null)
                {
                    continue;
                }

                if (_windowEnumerationService.TryGetCursorClientPosition(tab.TargetWindow, out _))
                {
                    targetWindow = tab.TargetWindow;
                    return true;
                }
            }

            return false;
        }

        private void GetAutoclickerMouseFlags(out uint downFlag, out uint upFlag)
        {
            if (AutoclickerMiddleRadioButton.IsChecked == true)
            {
                downFlag = MouseEventMiddleDown;
                upFlag = MouseEventMiddleUp;
                return;
            }

            if (AutoclickerRightRadioButton.IsChecked == true)
            {
                downFlag = MouseEventRightDown;
                upFlag = MouseEventRightUp;
                return;
            }

            downFlag = MouseEventLeftDown;
            upFlag = MouseEventLeftUp;
        }

        private void ApplyAutoclickerSettings()
        {
            _isLoadingAutoclickerSettings = true;
            try
            {
                _autoclickerHotkey = AppSettings.AutoclickerHotkey;
                AutoclickerHotkeyButton.Content = AppSettings.AutoclickerHotkey;
                AutoclickerClicksPerSecondTextBox.Text = AppSettings.AutoclickerClicksPerSecond.ToString("0.###", CultureInfo.CurrentCulture);
                AutoclickerStopAtTextBox.Text = AppSettings.AutoclickerStopAt <= 0
                    ? "never"
                    : AppSettings.AutoclickerStopAt.ToString(CultureInfo.CurrentCulture);
                AutoclickerPressRadioButton.IsChecked = string.Equals(AppSettings.AutoclickerMode, "Press", StringComparison.OrdinalIgnoreCase);
                AutoclickerToggleRadioButton.IsChecked = AutoclickerPressRadioButton.IsChecked != true;
                AutoclickerLeftRadioButton.IsChecked = string.Equals(AppSettings.AutoclickerMouseButton, "Left", StringComparison.OrdinalIgnoreCase);
                AutoclickerMiddleRadioButton.IsChecked = string.Equals(AppSettings.AutoclickerMouseButton, "Middle", StringComparison.OrdinalIgnoreCase);
                AutoclickerRightRadioButton.IsChecked = string.Equals(AppSettings.AutoclickerMouseButton, "Right", StringComparison.OrdinalIgnoreCase);

                if (AutoclickerLeftRadioButton.IsChecked != true
                    && AutoclickerMiddleRadioButton.IsChecked != true
                    && AutoclickerRightRadioButton.IsChecked != true)
                {
                    AutoclickerLeftRadioButton.IsChecked = true;
                }

                AutoclickerBroadcastCheckBox.IsChecked = AppSettings.AutoclickerBroadcastRelativeSpot;
            }
            finally
            {
                _isLoadingAutoclickerSettings = false;
            }
        }

        private void HookAutoclickerSettingsEvents()
        {
            AutoclickerClicksPerSecondTextBox.TextChanged += AutoclickerSetting_OnChanged;
            AutoclickerPressRadioButton.Checked += AutoclickerSetting_OnChanged;
            AutoclickerToggleRadioButton.Checked += AutoclickerSetting_OnChanged;
            AutoclickerLeftRadioButton.Checked += AutoclickerSetting_OnChanged;
            AutoclickerMiddleRadioButton.Checked += AutoclickerSetting_OnChanged;
            AutoclickerRightRadioButton.Checked += AutoclickerSetting_OnChanged;
            AutoclickerBroadcastCheckBox.Checked += AutoclickerSetting_OnChanged;
            AutoclickerBroadcastCheckBox.Unchecked += AutoclickerSetting_OnChanged;
        }

        private void AutoclickerSetting_OnChanged(object sender, RoutedEventArgs e)
        {
            SaveAutoclickerSettings();
        }

        private void SaveAutoclickerSettings()
        {
            if (_isLoadingAutoclickerSettings)
            {
                return;
            }

            AppSettings.AutoclickerHotkey = _autoclickerHotkey ?? "F8";
            AppSettings.AutoclickerStopAt = GetAutoclickerStopAt();
            AppSettings.AutoclickerMode = AutoclickerPressRadioButton.IsChecked == true ? "Press" : "Toggle";
            AppSettings.AutoclickerMouseButton = AutoclickerMiddleRadioButton.IsChecked == true
                ? "Middle"
                : AutoclickerRightRadioButton.IsChecked == true
                    ? "Right"
                    : "Left";
            AppSettings.AutoclickerBroadcastRelativeSpot = AutoclickerBroadcastCheckBox.IsChecked == true;

            if (double.TryParse(AutoclickerClicksPerSecondTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double clicksPerSecond)
                && clicksPerSecond > 0)
            {
                AppSettings.AutoclickerClicksPerSecond = clicksPerSecond;
            }

            AppSettings.Save();
        }

        private void StopAutoclicker(string statusMessage)
        {
            _isAutoclickerRunning = false;
            _autoclickerCancellation?.Cancel();
            _autoclickerCancellation = null;
            AutoclickerStatusText.Text = "status: idle";
            SetStatus(statusMessage);
        }

        private int GetAutoclickerStopAt()
        {
            return int.TryParse(AutoclickerStopAtTextBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int stopAt)
                ? Math.Max(0, stopAt)
                : 0;
        }

        private void AutoclickerStopAtTextBox_OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (string.Equals(AutoclickerStopAtTextBox.Text, "never", StringComparison.OrdinalIgnoreCase))
            {
                AutoclickerStopAtTextBox.Text = "0";
                AutoclickerStopAtTextBox.SelectAll();
            }
        }

        private void AutoclickerStopAtTextBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (GetAutoclickerStopAt() == 0)
            {
                AutoclickerStopAtTextBox.Text = "never";
            }

            SaveAutoclickerSettings();
        }

        private readonly record struct AutoclickSendResult(bool WasSent, string ErrorMessage)
        {
            public static AutoclickSendResult Sent()
            {
                return new AutoclickSendResult(true, string.Empty);
            }

            public static AutoclickSendResult Failed(string errorMessage)
            {
                return new AutoclickSendResult(false, errorMessage);
            }
        }

        #endregion

        #region Title Bar

        private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleWindowState();
                return;
            }

            _isDraggingTitleBar = true;
            _targetSyncTimer.Stop();
            _targetSyncUntil = null;

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                _isDraggingTitleBar = false;
                ClampTitleBarToCurrentMonitorVertically();
                ScheduleTargetSync();
            }
        }

        private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_OnClick(object sender, RoutedEventArgs e)
        {
            ToggleWindowState();
        }

        private void CloseButton_OnClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ToggleWindowState()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            UpdateMaximizeButtonIcon();
        }

        private void UpdateMaximizeButtonIcon()
        {
            if (MaximizeButton is null)
            {
                return;
            }

            MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }

        private void ClampTitleBarToCurrentMonitorVertically()
        {
            if (WindowState != WindowState.Normal)
            {
                return;
            }

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return;
            }

            NativeMonitorInfo monitorInfo = new()
            {
                Size = Marshal.SizeOf<NativeMonitorInfo>()
            };

            if (!GetMonitorInfo(monitor, ref monitorInfo))
            {
                return;
            }

            PresentationSource? source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is null)
            {
                return;
            }

            Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
            Point workAreaTopLeft = fromDevice.Transform(new Point(monitorInfo.WorkArea.Left, monitorInfo.WorkArea.Top));
            Point workAreaBottomRight = fromDevice.Transform(new Point(monitorInfo.WorkArea.Right, monitorInfo.WorkArea.Bottom));
            double titleBarHeight = Math.Min(TitleBarHeightPixels, Math.Max(1, ActualHeight));
            double maxTop = Math.Max(workAreaTopLeft.Y, workAreaBottomRight.Y - titleBarHeight);

            Top = Math.Clamp(Top, workAreaTopLeft.Y, maxTop);
        }

        #endregion

        #region Target Windowing

        private void TargetPickerButton_OnClick(object sender, RoutedEventArgs e)
        {
            ShowTargetPickerForActiveGameTab();
        }

        private void ShowTargetPickerForActiveGameTab()
        {
            EnsureDefaultGameTab();

            HashSet<nint> attachedTargetHandles = _gameTabs
                .Where(tab => tab.TargetWindow is not null)
                .Select(tab => tab.TargetWindow!.Handle)
                .ToHashSet();

            TargetPickerWindow picker = new(new WindowInteropHelper(this).Handle)
            {
                Owner = this,
                AttachedTargetHandles = attachedTargetHandles
            };

            if (picker.ShowDialog() == true && picker.SelectedWindow is not null)
            {
                AttachTargetWindow(picker.SelectedWindow);
            }
        }

        private void TargetPickerButton_OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_activeGameTab?.TargetWindow is null)
            {
                return;
            }

            DetachTargetWindow(_activeGameTab);
            e.Handled = true;
        }

        private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
        {
            SettingsWindow settingsWindow = new()
            {
                Owner = this
            };
            settingsWindow.ShowDialog();
        }

        private void ToolsMenuButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (ToolsMenuButton.ContextMenu is null)
            {
                return;
            }

            ToolsMenuButton.ContextMenu.PlacementTarget = ToolsMenuButton;
            ToolsMenuButton.ContextMenu.IsOpen = true;
        }

        private void BlackoutOverlayButton_OnClick(object sender, RoutedEventArgs e)
        {
            ToggleBlackoutOverlay();
        }

        private void ToggleBlackoutOverlay()
        {
            if (_blackoutOverlayWindow is { IsVisible: true })
            {
                _blackoutOverlayWindow.Close();
                return;
            }

            _blackoutOverlayWindow = new BlackoutOverlayWindow(new WindowInteropHelper(this).Handle);
            _blackoutOverlayWindow.Closed += (_, _) => _blackoutOverlayWindow = null;
            _blackoutOverlayWindow.Show();
        }

        public void AttachTargetWindow(WindowInfo targetWindow)
        {
            EnsureDefaultGameTab();

            _activeGameTab!.TargetWindow = targetWindow;
            _activeGameTab.DisplayName = targetWindow.ProcessName;
            _activeGameTab.IsRobloxTarget = IsRobloxTarget(targetWindow);
            SyncActiveGameTabToWindowState();
            _isTargetWorkspaceActive = true;
            _activeMacroEditorTab = null;
            ApplyWorkspaceLayout();

            SetTargetLinkedState(isLinked: true);

            if (_autoFollowWindow)
            {
                bool resizedTarget = ApplyTargetResolutionMode(targetWindow);
                SetStatus(resizedTarget
                    ? "Target linked and resized to fit the overlay."
                    : "Target linked.");
            }
            else
            {
                SetStatus("Target linked.");
            }

            TitleTargetStatusText.Text = targetWindow.DisplayName;
            UpdateTargetIndicator();
            RefreshWorkspaceTabs();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ScheduleTargetSync();
                BringTargetForwardForOverlay();
            }));
        }

        private void DetachTargetWindow(GameWorkspaceTab? tab = null)
        {
            tab ??= _activeGameTab;
            if (tab is null)
            {
                return;
            }

            bool detachedActiveTab = tab == _activeGameTab;
            tab.TargetWindow = null;
            tab.DisplayName = $"Game {tab.Id}";
            tab.IsRobloxTarget = false;

            if (detachedActiveTab)
            {
                SyncActiveGameTabToWindowState();
                TitleTargetStatusText.Text = "Target not attached";
            }

            SetTargetLinkedState(_gameTabs.Any(gameTab => gameTab.TargetWindow is not null));
            ApplyWorkspaceLayout();
            RefreshWorkspaceTabs();
            UpdateTargetIndicator();
            SetStatus("Target detached.");
        }

        private static bool IsRobloxTarget(WindowInfo targetWindow)
        {
            return targetWindow.ProcessName.Contains("Roblox", StringComparison.OrdinalIgnoreCase)
                   || targetWindow.Title.Contains("Roblox", StringComparison.OrdinalIgnoreCase);
        }

        private void ResolutionModeMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            _targetResolutionMode = sender is WpfMenuItem { Tag: string tag }
                ? TargetResolutionMode.FromTag(tag)
                : TargetResolutionMode.Follow;
            if (_activeGameTab is not null)
            {
                _activeGameTab.ResolutionMode = _targetResolutionMode;
            }

            if (_selectedTargetWindow is null)
            {
                return;
            }

            _isTargetSyncPaused = true;
            _targetSyncTimer.Stop();

            bool applied;
            try
            {
                applied = ApplyTargetResolutionMode(_selectedTargetWindow);
            }
            finally
            {
                _isTargetSyncPaused = false;
            }

            ScheduleTargetSync();
            SetStatus(applied
                ? $"Resolution mode applied: {_targetResolutionMode.DisplayName}."
                : $"Resolution mode selected: {_targetResolutionMode.DisplayName}.");
        }

        private bool FitOverlayAroundTarget(WindowInfo window)
        {
            PresentationSource? source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is null || ActualWidth <= 0 || ActualHeight <= 0)
            {
                return false;
            }

            WindowBounds targetBounds = GetTargetFitBounds(window);
            Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
            Point targetTopLeft = fromDevice.Transform(new Point(targetBounds.Left, targetBounds.Top));
            Point targetBottomRight = fromDevice.Transform(new Point(targetBounds.Right, targetBounds.Bottom));

            double targetWidth = targetBottomRight.X - targetTopLeft.X;
            double targetHeight = targetBottomRight.Y - targetTopLeft.Y;
            double horizontalChrome = Math.Max(0, ActualWidth - TargetViewport.ActualWidth);
            double verticalChrome = Math.Max(0, ActualHeight - TargetViewport.ActualHeight);
            double desiredWidth = Math.Max(MinWidth, targetWidth + horizontalChrome);
            double desiredHeight = Math.Max(MinHeight, targetHeight + verticalChrome);

            if (TryFitOverlayAndTargetToMonitor(window, desiredWidth, desiredHeight))
            {
                return true;
            }

            Width = desiredWidth;
            Height = desiredHeight;

            UpdateLayout();
            AlignOverlayViewportToBounds(targetBounds);
            ClampOverlayToTargetMonitor(window);
            UpdateWindowHoleRegion();
            return false;
        }

        private bool TryFitOverlayAndTargetToMonitor(WindowInfo window, double desiredWidth, double desiredHeight)
        {
            PresentationSource? source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is null || !_windowEnumerationService.TryGetMonitorWorkArea(window, out WindowBounds workArea))
            {
                return false;
            }

            Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
            Point workAreaTopLeft = fromDevice.Transform(new Point(workArea.Left, workArea.Top));
            Point workAreaBottomRight = fromDevice.Transform(new Point(workArea.Right, workArea.Bottom));
            double workAreaWidth = workAreaBottomRight.X - workAreaTopLeft.X;
            double workAreaHeight = workAreaBottomRight.Y - workAreaTopLeft.Y;

            if (desiredWidth <= workAreaWidth && desiredHeight <= workAreaHeight)
            {
                return false;
            }

            Left = workAreaTopLeft.X;
            Top = workAreaTopLeft.Y;
            Width = Math.Max(MinWidth, workAreaWidth);
            Height = Math.Max(MinHeight, workAreaHeight);

            UpdateLayout();
            ClampOverlayToTargetMonitor(window);
            UpdateWindowHoleRegion();

            return ResizeTargetToViewport(window);
        }

        private bool ApplyTargetResolutionMode(WindowInfo window)
        {
            if (!_targetResolutionMode.HasFixedSize)
            {
                return ResizeTargetToViewport(window);
            }

            if (!_windowEnumerationService.TryGetClientBounds(window, out WindowBounds clientBounds))
            {
                return ResizeTargetToViewport(window);
            }

            WindowBounds requestedBounds = new(
                clientBounds.Left,
                clientBounds.Top,
                clientBounds.Left + _targetResolutionMode.Width,
                clientBounds.Top + _targetResolutionMode.Height);

            bool resized = _windowEnumerationService.TryMoveWindowClientToBounds(window, requestedBounds);
            UpdateLayout();

            return ResizeTargetToViewport(window) || resized;
        }

        private bool ResizeTargetToViewport(WindowInfo window)
        {
            if (TargetViewport.ActualWidth <= 0 || TargetViewport.ActualHeight <= 0)
            {
                return false;
            }

            WindowBounds viewportBounds = GetViewportScreenBounds();

            WindowBounds clientBounds = GetViewportClientBounds(window, viewportBounds);

            return _windowEnumerationService.TryMoveWindowClientToBounds(window, clientBounds)
                   || _windowEnumerationService.TryMoveWindow(
                       window,
                       viewportBounds.Left,
                       viewportBounds.Top,
                       viewportBounds.Width,
                       viewportBounds.Height);
        }

        private bool ResizeTargetToElement(WindowInfo window, FrameworkElement element)
        {
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                return false;
            }

            WindowBounds viewportBounds = GetElementScreenBounds(element);
            WindowBounds clientBounds = GetViewportClientBounds(window, viewportBounds);

            return _windowEnumerationService.TryMoveWindowClientToBounds(window, clientBounds)
                   || _windowEnumerationService.TryMoveWindow(
                       window,
                       viewportBounds.Left,
                       viewportBounds.Top,
                       viewportBounds.Width,
                       viewportBounds.Height);
        }

        private WindowBounds GetTargetFitBounds(WindowInfo window)
        {
            if (_windowEnumerationService.TryGetClientBounds(window, out WindowBounds clientBounds))
            {
                return _windowEnumerationService.TryGetFrameInsets(window, out WindowFrameInsets insets)
                    ? new WindowBounds(
                        clientBounds.Left - TargetFrameBleedPixels,
                        clientBounds.Top - GetAdjustedTitleBarInset(insets) - TargetFrameBleedPixels,
                        clientBounds.Right + TargetFrameBleedPixels,
                        clientBounds.Bottom + TargetFrameBleedPixels)
                    : ExpandBounds(clientBounds, TargetFrameBleedPixels);
            }

            return _windowEnumerationService.TryGetWindowBounds(window, out WindowBounds bounds)
                ? ExpandBounds(bounds, TargetFrameBleedPixels)
                : new WindowBounds(window.Left, window.Top, window.Right, window.Bottom);
        }

        private WindowBounds GetViewportClientBounds(WindowInfo window, WindowBounds viewportBounds)
        {
            WindowBounds insetViewportBounds = ContractBounds(viewportBounds, TargetFrameBleedPixels);

            if (!_windowEnumerationService.TryGetFrameInsets(window, out WindowFrameInsets insets)
                || GetAdjustedTitleBarInset(insets) <= 0
                || insetViewportBounds.Height <= GetAdjustedTitleBarInset(insets))
            {
                return insetViewportBounds;
            }

            int titleBarInset = GetAdjustedTitleBarInset(insets);

            return new WindowBounds(
                insetViewportBounds.Left,
                insetViewportBounds.Top + titleBarInset,
                insetViewportBounds.Right,
                insetViewportBounds.Bottom);
        }

        private static WindowBounds ExpandBounds(WindowBounds bounds, int pixels)
        {
            return new WindowBounds(
                bounds.Left - pixels,
                bounds.Top - pixels,
                bounds.Right + pixels,
                bounds.Bottom + pixels);
        }

        private static WindowBounds ContractBounds(WindowBounds bounds, int pixels)
        {
            if (bounds.Width <= pixels * 2 || bounds.Height <= pixels * 2)
            {
                return bounds;
            }

            return new WindowBounds(
                bounds.Left + pixels,
                bounds.Top + pixels,
                bounds.Right - pixels,
                bounds.Bottom - pixels);
        }

        private static int GetAdjustedTitleBarInset(WindowFrameInsets insets)
        {
            return Math.Max(0, insets.Top - TargetTitleBarFitAdjustmentPixels);
        }

        private void AlignOverlayViewportToBounds(WindowBounds bounds)
        {
            PresentationSource? source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is null)
            {
                return;
            }

            Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
            Point targetTopLeft = fromDevice.Transform(new Point(bounds.Left, bounds.Top));
            Point viewportTopLeft = TargetViewport.TransformToAncestor(this).Transform(new Point(0, 0));

            Left = targetTopLeft.X - viewportTopLeft.X;
            Top = targetTopLeft.Y - viewportTopLeft.Y;
        }

        private void ClampOverlayToTargetMonitor(WindowInfo window)
        {
            PresentationSource? source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is null
                || !_windowEnumerationService.TryGetMonitorWorkArea(window, out WindowBounds workArea)
                || ActualWidth <= 0
                || ActualHeight <= 0)
            {
                return;
            }

            Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
            Point workAreaTopLeft = fromDevice.Transform(new Point(workArea.Left, workArea.Top));
            Point workAreaBottomRight = fromDevice.Transform(new Point(workArea.Right, workArea.Bottom));

            double maxLeft = Math.Max(workAreaTopLeft.X, workAreaBottomRight.X - ActualWidth);
            double maxTop = Math.Max(workAreaTopLeft.Y, workAreaBottomRight.Y - ActualHeight);

            Left = Math.Clamp(Left, workAreaTopLeft.X, maxLeft);
            Top = Math.Clamp(Top, workAreaTopLeft.Y, maxTop);
        }

        private WindowBounds GetViewportScreenBounds()
        {
            return GetElementScreenBounds(TargetViewport);
        }

        private static WindowBounds GetElementScreenBounds(FrameworkElement element)
        {
            Point viewportTopLeft = element.PointToScreen(new Point(0, 0));
            Point viewportBottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));

            int left = (int)Math.Round(viewportTopLeft.X);
            int top = (int)Math.Round(viewportTopLeft.Y);
            int right = (int)Math.Round(viewportBottomRight.X);
            int bottom = (int)Math.Round(viewportBottomRight.Y);

            return new WindowBounds(left, top, right, bottom);
        }

        private static bool AreBoundsClose(WindowBounds first, WindowBounds second)
        {
            const int tolerance = 1;

            return Math.Abs(first.Left - second.Left) <= tolerance
                   && Math.Abs(first.Top - second.Top) <= tolerance
                   && Math.Abs(first.Right - second.Right) <= tolerance
                   && Math.Abs(first.Bottom - second.Bottom) <= tolerance;
        }

        private void SetTargetLinkedState(bool isLinked)
        {
            SetCenterAcrylicEnabled(!isLinked);
        }

        private void ClientLayoutFlyoutButton_OnClick(object sender, RoutedEventArgs e)
        {
            ContextMenu menu = new();
            AddClientLayoutMenuItem(menu, "1 x 1", ClientLayoutMode.OneByOne);
            AddClientLayoutMenuItem(menu, "2 x 1", ClientLayoutMode.TwoColumns);
            AddClientLayoutMenuItem(menu, "1 x 2", ClientLayoutMode.TwoRows);
            AddClientLayoutMenuItem(menu, "2 x 2", ClientLayoutMode.TwoByTwo);
            AddClientLayoutMenuItem(menu, "Left Stack", ClientLayoutMode.LeftStack);
            AddClientLayoutMenuItem(menu, "Right Stack", ClientLayoutMode.RightStack);

            ClientLayoutFlyoutButton.ContextMenu = menu;
            menu.PlacementTarget = ClientLayoutFlyoutButton;
            menu.IsOpen = true;
        }

        private void AddClientLayoutMenuItem(ContextMenu menu, string header, ClientLayoutMode mode)
        {
            WpfMenuItem item = new()
            {
                Header = header,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                IsCheckable = true,
                IsChecked = _clientLayoutMode == mode,
                Tag = mode
            };
            item.Click += (_, _) =>
            {
                _clientLayoutMode = mode;
                ApplyWorkspaceLayout();
                RefreshWorkspaceTabs();
                SetStatus($"Client layout set to {GetClientLayoutLabel(mode)}.");
            };
            menu.Items.Add(item);
        }

        private void BringTargetForwardForOverlay()
        {
            if (_selectedTargetWindow is null || !IsActive)
            {
                return;
            }

            nint overlayHandle = new WindowInteropHelper(this).Handle;
            _windowEnumerationService.TryPlaceWindowBehind(_selectedTargetWindow, overlayHandle);
        }

        private void UpdateTargetIndicator()
        {
            if (_selectedTargetWindow is null)
            {
                TargetPickerButton.ToolTip = "Attach target";
                StartTargetRainbowAnimation();
                return;
            }

            _targetPulseTimer.Stop();
            TargetPickerButton.BeginAnimation(OpacityProperty, null);
            TargetPickerButton.Opacity = 1;
            TargetPickerButton.ToolTip = "Click to change target. Right-click to detach.";
            SetTargetIndicatorColor(Color.FromRgb(86, 232, 122));
        }

        private void TargetPulseTimer_OnTick(object? sender, EventArgs e)
        {
            StartTargetRainbowAnimation();
        }

        private void SetTargetIndicatorColor(Color color)
        {
            SolidColorBrush brush = new(color);
            TargetPickerButton.Foreground = brush;
        }

        private void StartTargetRainbowAnimation()
        {
            SolidColorBrush brush = new(Color.FromRgb(255, 74, 74));
            TargetPickerButton.Foreground = brush;

            ColorAnimationUsingKeyFrames animation = new()
            {
                Duration = TimeSpan.FromSeconds(5.6),
                RepeatBehavior = RepeatBehavior.Forever
            };

            animation.KeyFrames.Add(new LinearColorKeyFrame(Color.FromRgb(255, 74, 74), KeyTime.FromPercent(0)));
            animation.KeyFrames.Add(new LinearColorKeyFrame(Color.FromRgb(255, 190, 70), KeyTime.FromPercent(0.17)));
            animation.KeyFrames.Add(new LinearColorKeyFrame(Color.FromRgb(255, 245, 90), KeyTime.FromPercent(0.33)));
            animation.KeyFrames.Add(new LinearColorKeyFrame(Color.FromRgb(88, 236, 116), KeyTime.FromPercent(0.5)));
            animation.KeyFrames.Add(new LinearColorKeyFrame(Color.FromRgb(72, 164, 255), KeyTime.FromPercent(0.67)));
            animation.KeyFrames.Add(new LinearColorKeyFrame(Color.FromRgb(184, 98, 255), KeyTime.FromPercent(0.83)));
            animation.KeyFrames.Add(new LinearColorKeyFrame(Color.FromRgb(255, 74, 74), KeyTime.FromPercent(1)));

            brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        }

        private void SetCenterAcrylicEnabled(bool isEnabled)
        {
            CenterAcrylicToggle.IsChecked = isEnabled;
            UpdateWindowHoleRegion();
        }

        #endregion

        #region Macro Library

        private void NewMacroButton_OnClick(object sender, RoutedEventArgs e)
        {
            CreateMacro(GetSelectedDirectoryPath());
        }

        private void NewMacroMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            CreateMacro(GetSelectedDirectoryPath());
        }

        private void OpenMacroButton_OnClick(object sender, RoutedEventArgs e)
        {
            OpenSelectedMacro();
        }

        private void OpenMacroMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            OpenSelectedMacro();
        }

        private void SaveMacroButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (_activeMacroEditorTab is not null)
            {
                SaveActiveEditorTab();
                return;
            }

            if (_currentMacroPath is null)
            {
                _currentMacroPath = _macroLibraryService.CreateMacro(GetSelectedDirectoryPath());
                RefreshMacroTree();
            }

            _macroLibraryService.SaveMacro(_currentMacroPath);
            SetCurrentMacroText(_currentMacroPath);
            SetStatus("Macro saved.");
        }

        private void SaveMacroMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            SaveMacroButton_OnClick(sender, e);
        }

        private void RefreshMacroTreeButton_OnClick(object sender, RoutedEventArgs e)
        {
            RefreshMacroTree();
            SetStatus("Macro library refreshed.");
        }

        private void MacroTreeView_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
        }

        private void MacroTreeView_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            MacroTreeItem? item = GetSelectedMacroItem();
            if (item is null || item.IsDirectory)
            {
                return;
            }

            OpenMacro(item.FullPath);
            e.Handled = true;
        }

        private void RefreshMacroTree()
        {
            HashSet<string> expandedPaths = GetExpandedMacroTreePaths();
            string? selectedPath = GetSelectedMacroItem()?.FullPath ?? _currentMacroPath;

            MacroTreeView.Items.Clear();
            MacroTreeView.Items.Add(CreateTreeViewItem(_macroLibraryService.LoadTree(), expandedPaths, selectedPath));
        }

        private WpfTreeViewItem CreateTreeViewItem(MacroTreeItem item, ISet<string>? expandedPaths = null, string? selectedPath = null)
        {
            WpfTreeViewItem treeViewItem = new()
            {
                Header = item.Name,
                Tag = item,
                IsExpanded = item.FullPath == _macroLibraryService.RootDirectory
                             || expandedPaths?.Contains(item.FullPath) == true,
                IsSelected = string.Equals(item.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase),
                ContextMenu = CreateMacroContextMenu(item)
            };

            foreach (MacroTreeItem child in item.Children)
            {
                treeViewItem.Items.Add(CreateTreeViewItem(child, expandedPaths, selectedPath));
            }

            return treeViewItem;
        }

        private HashSet<string> GetExpandedMacroTreePaths()
        {
            HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);

            foreach (object item in MacroTreeView.Items)
            {
                if (item is WpfTreeViewItem treeViewItem)
                {
                    CollectExpandedMacroTreePaths(treeViewItem, paths);
                }
            }

            return paths;
        }

        private static void CollectExpandedMacroTreePaths(WpfTreeViewItem treeViewItem, ISet<string> paths)
        {
            if (treeViewItem.Tag is MacroTreeItem { IsDirectory: true } item && treeViewItem.IsExpanded)
            {
                paths.Add(item.FullPath);
            }

            foreach (object child in treeViewItem.Items)
            {
                if (child is WpfTreeViewItem childTreeViewItem)
                {
                    CollectExpandedMacroTreePaths(childTreeViewItem, paths);
                }
            }
        }

        private ContextMenu CreateMacroContextMenu(MacroTreeItem item)
        {
            ContextMenu menu = new();
            bool isRoot = string.Equals(item.FullPath, _macroLibraryService.RootDirectory, StringComparison.OrdinalIgnoreCase);

            if (item.IsDirectory)
            {
                menu.Items.Add(CreateContextMenuItem("New Macro", (_, _) => CreateMacro(item.FullPath)));
                menu.Items.Add(CreateContextMenuItem("New Folder", (_, _) => CreateFolder(item.FullPath)));
            }
            else
            {
                menu.Items.Add(CreateContextMenuItem("Open", (_, _) => OpenMacro(item.FullPath)));
            }

            if (!isRoot)
            {
                menu.Items.Add(CreateContextMenuItem("Rename", (_, _) => RenameMacroItem(item)));
                menu.Items.Add(CreateContextMenuItem("Delete", (_, _) => DeleteMacroItem(item)));
            }

            menu.Items.Add(CreateContextMenuItem("Refresh", (_, _) => RefreshMacroTree()));

            return menu;
        }

        private static WpfMenuItem CreateContextMenuItem(string header, RoutedEventHandler clickHandler)
        {
            WpfMenuItem menuItem = new() { Header = header };
            menuItem.Click += clickHandler;
            return menuItem;
        }

        private void CreateMacro(string? directoryPath)
        {
            _currentMacroPath = _macroLibraryService.CreateMacro(directoryPath);
            RefreshMacroTree();
            SetStatus("Macro created.");
        }

        private void CreateFolder(string? directoryPath)
        {
            string? name = AskForText("New Folder", "Folder name:", "New Folder");
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            _macroLibraryService.CreateFolder(directoryPath, name);
            RefreshMacroTree();
            SetStatus("Folder created.");
        }

        private void RenameMacroItem(MacroTreeItem item)
        {
            string? name = AskForText("Rename", "New name:", item.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            string renamedPath = _macroLibraryService.Rename(item.FullPath, name);
            if (_currentMacroPath == item.FullPath)
            {
                _currentMacroPath = renamedPath;
                SetCurrentMacroText(renamedPath);
            }

            RefreshMacroTree();
            SetStatus("Item renamed.");
        }

        private void DeleteMacroItem(MacroTreeItem item)
        {
            System.Windows.MessageBoxResult result = System.Windows.MessageBox.Show(
                this,
                $"Delete '{item.Name}'?",
                "Delete Macro Item",
                System.Windows.MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            _macroLibraryService.Delete(item.FullPath);

            if (_currentMacroPath == item.FullPath)
            {
                _currentMacroPath = null;
                SetCurrentMacroText(null);
            }

            RefreshMacroTree();
            SetStatus("Item deleted.");
        }

        private void OpenSelectedMacro()
        {
            MacroTreeItem? item = GetSelectedMacroItem();
            if (item is null || item.IsDirectory)
            {
                SetStatus("Select a macro file first.");
                return;
            }

            OpenMacro(item.FullPath);
        }

        private void OpenMacro(string path)
        {
            if (!_macroLibraryService.IsMacroFile(path))
            {
                SetStatus("Selected item is not a macro file.");
                return;
            }

            _currentMacroPath = path;
            SetCurrentMacroText(path);
            OpenMacroEditorTab(path);
            SetStatus("Macro opened in editor.");
        }

        private MacroTreeItem? GetSelectedMacroItem()
        {
            return (MacroTreeView.SelectedItem as WpfTreeViewItem)?.Tag as MacroTreeItem;
        }

        private string? GetSelectedDirectoryPath()
        {
            MacroTreeItem? item = GetSelectedMacroItem();
            if (item is null)
            {
                return _macroLibraryService.RootDirectory;
            }

            return item.IsDirectory ? item.FullPath : Path.GetDirectoryName(item.FullPath);
        }

        private string? AskForText(string title, string prompt, string value)
        {
            InputDialog dialog = new(title, prompt, value)
            {
                Owner = this
            };

            return dialog.ShowDialog() == true ? dialog.ResponseText : null;
        }

        #endregion

        #region Workspace Tabs

        private void EnsureDefaultGameTab()
        {
            if (_gameTabs.Count == 0)
            {
                _gameTabs.Add(new GameWorkspaceTab(1, "Game 1"));
            }

            _activeGameTab ??= _gameTabs[0];
        }

        private void EnsureGameTabsForCurrentLayout()
        {
            EnsureDefaultGameTab();

            if (_clientLayoutMode == ClientLayoutMode.OneByOne)
            {
                return;
            }

            int paneCount = GetPanePlacements(_clientLayoutMode).Count;
            while (_gameTabs.Count < paneCount)
            {
                int nextId = _gameTabs.Count == 0 ? 1 : _gameTabs.Max(tab => tab.Id) + 1;
                _gameTabs.Add(new GameWorkspaceTab(nextId, $"Game {nextId}"));
            }
        }

        private void SyncActiveGameTabToWindowState()
        {
            _selectedTargetWindow = _activeGameTab?.TargetWindow;
            _targetResolutionMode = _activeGameTab?.ResolutionMode ?? TargetResolutionMode.Follow;
        }

        private void TargetWorkspaceTabButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is WpfButton { Tag: GameWorkspaceTab tab })
            {
                ActivateTargetWorkspace(tab);
                return;
            }

            ActivateTargetWorkspace(_activeGameTab);
        }

        private void AddGameWorkspaceTabButton_OnClick(object sender, RoutedEventArgs e)
        {
            int nextId = _gameTabs.Count == 0 ? 1 : _gameTabs.Max(tab => tab.Id) + 1;
            GameWorkspaceTab tab = new(nextId, $"Game {nextId}");
            _gameTabs.Add(tab);
            ActivateTargetWorkspace(tab);
            ShowTargetPickerForActiveGameTab();
        }

        private void NewEditorTabButton_OnClick(object sender, RoutedEventArgs e)
        {
            CreateMacro(GetSelectedDirectoryPath());
            if (_currentMacroPath is not null)
            {
                OpenMacroEditorTab(_currentMacroPath);
            }
        }

        private void SetCurrentMacroText(string? path)
        {
            CurrentFileText.Text = path is null
                ? "No macro selected"
                : Path.GetFileName(path);
        }

        private void OpenMacroEditorTab(string path)
        {
            MacroEditorTab? tab = _macroEditorTabs.FirstOrDefault(item => string.Equals(item.FilePath, path, StringComparison.OrdinalIgnoreCase));

            if (tab is null)
            {
                tab = new MacroEditorTab(path, Path.GetFileName(path), _macroLibraryService.ReadMacroText(path));
                _macroEditorTabs.Add(tab);
            }

            ActivateMacroEditorTab(tab);
        }

        private void ActivateTargetWorkspace(GameWorkspaceTab? tab = null)
        {
            EnsureDefaultGameTab();
            _activeGameTab = tab ?? _activeGameTab;
            SyncActiveGameTabToWindowState();
            _isTargetWorkspaceActive = true;
            _activeMacroEditorTab = null;
            ApplyWorkspaceLayout();
            RefreshWorkspaceTabs();
            BringTargetForwardForOverlay();
        }

        private void ActivateMacroEditorTab(MacroEditorTab tab)
        {
            _isTargetWorkspaceActive = false;
            _activeMacroEditorTab = tab;

            _isUpdatingEditorText = true;
            MacroEditorTextBox.Text = tab.Text;
            _isUpdatingEditorText = false;

            ApplyWorkspaceLayout();
            RefreshWorkspaceTabs();
        }

        private void ApplyWorkspaceLayout()
        {
            ClearClientLayoutFrames();
            ClearMultiEditorHosts();
            ClearGamePaneHosts();
            ClearEmptyPaneHosts();
            WorkspaceSurfaceGrid.RowDefinitions.Clear();
            WorkspaceSurfaceGrid.ColumnDefinitions.Clear();
            EnsureGameTabsForCurrentLayout();
            UpdateWorkspaceTabsChromeVisibility();
            ApplyClientLayoutGrid();

            Grid.SetRow(TargetViewport, 0);
            Grid.SetColumn(TargetViewport, 0);
            Grid.SetRowSpan(TargetViewport, 1);
            Grid.SetColumnSpan(TargetViewport, 1);
            Grid.SetRow(MacroEditorHost, 0);
            Grid.SetColumn(MacroEditorHost, 0);
            Grid.SetRowSpan(MacroEditorHost, 1);
            Grid.SetColumnSpan(MacroEditorHost, 1);

            if (_activeMacroEditorTab is not null && _activeMacroEditorTab.DockSlot != DockSlot.Full)
            {
                ApplySplitDockLayout(_activeMacroEditorTab.DockSlot, dockSlotIsEditor: true);
                TargetViewport.Visibility = Visibility.Visible;
                MacroEditorHost.Visibility = Visibility.Visible;
                SyncTargetAfterDockLayout();
                return;
            }

            if (_activeMacroEditorTab is not null && _activeGameTab?.DockSlot != DockSlot.Full)
            {
                ApplySplitDockLayout(_activeGameTab!.DockSlot, dockSlotIsEditor: false);
                TargetViewport.Visibility = Visibility.Visible;
                MacroEditorHost.Visibility = Visibility.Visible;
                SyncTargetAfterDockLayout();
                return;
            }

            if (_clientLayoutMode != ClientLayoutMode.OneByOne
                && _activeMacroEditorTab is null)
            {
                TargetViewport.Visibility = Visibility.Collapsed;
                MacroEditorHost.Visibility = Visibility.Collapsed;
                _activeMacroEditorTab = null;
                RenderMultiGameLayout();
                SyncTargetAfterDockLayout();
                return;
            }

            if (_clientLayoutMode != ClientLayoutMode.OneByOne
                && _macroEditorTabs.Count > 1)
            {
                TargetViewport.Visibility = Visibility.Collapsed;
                MacroEditorHost.Visibility = Visibility.Collapsed;
                RenderMultiEditorLayout();
                ClearWindowHoleRegion(new WindowInteropHelper(this).Handle);
                return;
            }

            TargetViewport.Visibility = Visibility.Visible;
            MacroEditorHost.Visibility = _activeMacroEditorTab is null ? Visibility.Collapsed : Visibility.Visible;

            if (_activeMacroEditorTab is null)
            {
                SyncTargetAfterDockLayout();
            }
            else
            {
                ClearWindowHoleRegion(new WindowInteropHelper(this).Handle);
            }
        }

        private void ApplyClientLayoutGrid()
        {
            switch (_clientLayoutMode)
            {
                case ClientLayoutMode.TwoColumns:
                    AddWorkspaceColumns(2);
                    AddWorkspaceRows(1);
                    AddClientLayoutFrame(0, 0);
                    AddClientLayoutFrame(0, 1);
                    break;
                case ClientLayoutMode.TwoRows:
                    AddWorkspaceColumns(1);
                    AddWorkspaceRows(2);
                    AddClientLayoutFrame(0, 0);
                    AddClientLayoutFrame(1, 0);
                    break;
                case ClientLayoutMode.TwoByTwo:
                    AddWorkspaceColumns(2);
                    AddWorkspaceRows(2);
                    AddClientLayoutFrame(0, 0);
                    AddClientLayoutFrame(0, 1);
                    AddClientLayoutFrame(1, 0);
                    AddClientLayoutFrame(1, 1);
                    break;
                case ClientLayoutMode.LeftStack:
                case ClientLayoutMode.RightStack:
                    AddWorkspaceColumns(2);
                    AddWorkspaceRows(2);
                    AddClientLayoutFrame(0, 0);
                    AddClientLayoutFrame(1, 0);
                    AddClientLayoutFrame(0, 1, rowSpan: 2);
                    break;
                default:
                    AddWorkspaceColumns(1);
                    AddWorkspaceRows(1);
                    break;
            }
        }

        private void AddWorkspaceColumns(int count)
        {
            for (int i = 0; i < count; i++)
            {
                WorkspaceSurfaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
        }

        private void AddWorkspaceRows(int count)
        {
            for (int i = 0; i < count; i++)
            {
                WorkspaceSurfaceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            }
        }

        private void AddClientLayoutFrame(int row, int column, int rowSpan = 1, int columnSpan = 1)
        {
            Border frame = new()
            {
                Margin = new Thickness(4),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 120, 120, 120)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
                IsHitTestVisible = false
            };

            Grid.SetRow(frame, row);
            Grid.SetColumn(frame, column);
            Grid.SetRowSpan(frame, rowSpan);
            Grid.SetColumnSpan(frame, columnSpan);
            _clientLayoutFrames.Add(frame);
            WorkspaceSurfaceGrid.Children.Insert(0, frame);
        }

        private void ClearClientLayoutFrames()
        {
            foreach (Border frame in _clientLayoutFrames)
            {
                WorkspaceSurfaceGrid.Children.Remove(frame);
            }

            _clientLayoutFrames.Clear();
        }

        private void ClearMultiEditorHosts()
        {
            foreach (Grid host in _multiEditorHosts)
            {
                WorkspaceSurfaceGrid.Children.Remove(host);
            }

            _multiEditorHosts.Clear();
        }

        private void ClearGamePaneHosts()
        {
            foreach (Grid host in _gamePaneHosts)
            {
                WorkspaceSurfaceGrid.Children.Remove(host);
            }

            _gamePaneHosts.Clear();
            _gamePaneTargets.Clear();
        }

        private void ClearEmptyPaneHosts()
        {
            foreach (Grid host in _emptyPaneHosts)
            {
                WorkspaceSurfaceGrid.Children.Remove(host);
            }

            _emptyPaneHosts.Clear();
        }

        private void RenderEmptyPaneLayout()
        {
            foreach (PanePlacement placement in GetPanePlacements(_clientLayoutMode))
            {
                Grid host = CreateEmptyPaneHost();
                Grid.SetRow(host, placement.Row);
                Grid.SetColumn(host, placement.Column);
                Grid.SetRowSpan(host, placement.RowSpan);
                Grid.SetColumnSpan(host, placement.ColumnSpan);
                _emptyPaneHosts.Add(host);
                WorkspaceSurfaceGrid.Children.Add(host);
            }
        }

        private Grid CreateEmptyPaneHost()
        {
            Grid host = new()
            {
                Margin = new Thickness(4),
                Background = new SolidColorBrush(Color.FromRgb(43, 43, 43))
            };
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Border tabStrip = new()
            {
                Background = new SolidColorBrush(Color.FromRgb(26, 26, 26)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(64, 82, 82, 82)),
                BorderThickness = new Thickness(1, 1, 1, 0),
                CornerRadius = new CornerRadius(7, 7, 0, 0),
                Child = new System.Windows.Controls.TextBlock
                {
                    Margin = new Thickness(10, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12,
                    Foreground = (Brush)FindResource("TextMutedBrush"),
                    Text = "Empty"
                }
            };
            host.Children.Add(tabStrip);

            Border surface = new()
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(95, 130, 130, 130)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0, 0, 8, 8),
                Background = new SolidColorBrush(Color.FromRgb(46, 46, 46))
            };
            Grid.SetRow(surface, 1);
            host.Children.Add(surface);

            return host;
        }

        private void RenderMultiGameLayout()
        {
            List<PanePlacement> placements = GetPanePlacements(_clientLayoutMode);
            List<GameWorkspaceTab> paneTabs = _gameTabs.Take(placements.Count).ToList();

            for (int i = 0; i < paneTabs.Count; i++)
            {
                GameWorkspaceTab tab = paneTabs[i];
                PanePlacement placement = placements[i];
                Grid host = CreateGamePaneHost(tab, out Border targetSurface);

                Grid.SetRow(host, placement.Row);
                Grid.SetColumn(host, placement.Column);
                Grid.SetRowSpan(host, placement.RowSpan);
                Grid.SetColumnSpan(host, placement.ColumnSpan);
                _gamePaneHosts.Add(host);
                if (tab.TargetWindow is not null)
                {
                    _gamePaneTargets[tab] = targetSurface;
                }
                WorkspaceSurfaceGrid.Children.Add(host);
            }
        }

        private Grid CreateGamePaneHost(GameWorkspaceTab tab, out Border targetSurface)
        {
            Grid host = new()
            {
                Margin = new Thickness(4),
                Background = new SolidColorBrush(Color.FromRgb(43, 43, 43))
            };
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            StackPanel tabStrip = new()
            {
                Orientation = Orientation.Horizontal,
                Background = new SolidColorBrush(Color.FromRgb(26, 26, 26))
            };

            WpfButton tabButton = new()
            {
                Content = tab.TargetWindow?.ProcessName ?? tab.DisplayName,
                Style = (Style)FindResource(tab == _activeGameTab
                    ? "WorkspaceTabActiveButtonStyle"
                    : "WorkspaceTabButtonStyle"),
                MinWidth = 92,
                Height = 27,
                Padding = new Thickness(10, 0, 10, 0),
                Tag = tab,
                ContextMenu = CreateTargetTabContextMenu(tab)
            };
            tabButton.Click += (_, _) => ActivateTargetWorkspace(tab);
            tabStrip.Children.Add(tabButton);
            host.Children.Add(tabStrip);

            targetSurface = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(95, 130, 130, 130)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0, 0, 8, 8),
                Background = new SolidColorBrush(Color.FromRgb(46, 46, 46)),
                ToolTip = tab.TargetWindow?.DisplayName ?? tab.DisplayName
            };
            targetSurface.MouseLeftButtonDown += (_, _) => ActivateTargetWorkspace(tab);
            Grid.SetRow(targetSurface, 1);
            host.Children.Add(targetSurface);

            return host;
        }

        private void RenderMultiEditorLayout()
        {
            List<PanePlacement> placements = GetPanePlacements(_clientLayoutMode);
            int paneCount = Math.Min(placements.Count, _macroEditorTabs.Count);

            for (int i = 0; i < paneCount; i++)
            {
                MacroEditorTab tab = _macroEditorTabs[i];
                PanePlacement placement = placements[i];
                Grid host = CreatePaneEditorHost(tab);

                Grid.SetRow(host, placement.Row);
                Grid.SetColumn(host, placement.Column);
                Grid.SetRowSpan(host, placement.RowSpan);
                Grid.SetColumnSpan(host, placement.ColumnSpan);
                _multiEditorHosts.Add(host);
                WorkspaceSurfaceGrid.Children.Add(host);
            }
        }

        private Grid CreatePaneEditorHost(MacroEditorTab tab)
        {
            Grid host = new()
            {
                Margin = new Thickness(4),
                Background = new SolidColorBrush(Color.FromRgb(16, 16, 16))
            };
            host.MouseLeftButtonDown += (_, _) => ActivateMacroEditorTab(tab);
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border tabHeader = new()
            {
                Background = new SolidColorBrush(Color.FromRgb(36, 36, 36)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(64, 82, 82, 82)),
                BorderThickness = new Thickness(1, 1, 1, 0),
                CornerRadius = new CornerRadius(7, 7, 0, 0),
                Child = new System.Windows.Controls.TextBlock
                {
                    Margin = new Thickness(10, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12,
                    FontWeight = tab == _activeMacroEditorTab ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = Brushes.White,
                    Text = tab.DisplayName + (tab.IsDirty ? " *" : string.Empty),
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
            tabHeader.MouseLeftButtonDown += (_, _) => ActivateMacroEditorTab(tab);
            Grid.SetColumnSpan(tabHeader, 2);
            host.Children.Add(tabHeader);

            Border lineColumn = new()
            {
                Background = new SolidColorBrush(Color.FromRgb(21, 21, 21)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(38, 63, 63, 63)),
                BorderThickness = new Thickness(0, 0, 1, 0),
                Child = new System.Windows.Controls.TextBlock
                {
                    Margin = new Thickness(0, 10, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
                    Text = "1"
                }
            };
            Grid.SetRow(lineColumn, 1);
            host.Children.Add(lineColumn);

            System.Windows.Controls.TextBox editor = new()
            {
                AcceptsReturn = true,
                AcceptsTab = true,
                Background = new SolidColorBrush(Color.FromRgb(16, 16, 16)),
                BorderThickness = new Thickness(0),
                CaretBrush = Brushes.White,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(230, 230, 230)),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                Padding = new Thickness(14, 10, 14, 10),
                Text = tab.Text,
                TextWrapping = TextWrapping.NoWrap,
                Tag = tab
            };
            editor.TextChanged += PaneEditorTextBox_OnTextChanged;
            Grid.SetRow(editor, 1);
            Grid.SetColumn(editor, 1);
            host.Children.Add(editor);

            return host;
        }

        private void PaneEditorTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not System.Windows.Controls.TextBox { Tag: MacroEditorTab tab } textBox)
            {
                return;
            }

            tab.Text = textBox.Text;
            tab.IsDirty = true;
            if (tab == _activeMacroEditorTab && MacroEditorTextBox.Text != tab.Text)
            {
                _isUpdatingEditorText = true;
                MacroEditorTextBox.Text = tab.Text;
                _isUpdatingEditorText = false;
            }
            RefreshWorkspaceTabs();
        }

        private static List<PanePlacement> GetPanePlacements(ClientLayoutMode mode)
        {
            return mode switch
            {
                ClientLayoutMode.TwoColumns =>
                [
                    new(0, 0),
                    new(0, 1)
                ],
                ClientLayoutMode.TwoRows =>
                [
                    new(0, 0),
                    new(1, 0)
                ],
                ClientLayoutMode.TwoByTwo =>
                [
                    new(0, 0),
                    new(0, 1),
                    new(1, 0),
                    new(1, 1)
                ],
                ClientLayoutMode.LeftStack =>
                [
                    new(0, 0),
                    new(1, 0),
                    new(0, 1, RowSpan: 2)
                ],
                ClientLayoutMode.RightStack =>
                [
                    new(0, 1),
                    new(1, 1),
                    new(0, 0, RowSpan: 2)
                ],
                _ =>
                [
                    new(0, 0)
                ]
            };
        }

        private void PlaceMacroEditorInSecondaryFrame()
        {
            switch (_clientLayoutMode)
            {
                case ClientLayoutMode.TwoColumns:
                    Grid.SetColumn(MacroEditorHost, 1);
                    break;
                case ClientLayoutMode.TwoRows:
                    Grid.SetRow(MacroEditorHost, 1);
                    break;
                case ClientLayoutMode.TwoByTwo:
                    Grid.SetColumn(MacroEditorHost, 1);
                    break;
                case ClientLayoutMode.LeftStack:
                    Grid.SetColumn(TargetViewport, 1);
                    Grid.SetRowSpan(TargetViewport, 2);
                    Grid.SetRow(MacroEditorHost, 0);
                    Grid.SetColumn(MacroEditorHost, 0);
                    break;
                case ClientLayoutMode.RightStack:
                    Grid.SetRowSpan(TargetViewport, 2);
                    Grid.SetColumn(MacroEditorHost, 1);
                    break;
            }
        }

        private void SyncTargetAfterDockLayout()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                WorkspaceSurfaceGrid.UpdateLayout();
                TargetViewport.UpdateLayout();
                UpdateWindowHoleRegion();
                SyncTargetWindowToViewport();
            }), DispatcherPriority.Loaded);
        }

        private void ApplySplitDockLayout(DockSlot dockSlot, bool dockSlotIsEditor)
        {
            DockSlot editorSlot = dockSlotIsEditor ? dockSlot : GetOppositeDockSlot(dockSlot);

            WorkspaceSurfaceGrid.RowDefinitions.Clear();
            WorkspaceSurfaceGrid.ColumnDefinitions.Clear();

            if (editorSlot is DockSlot.Left or DockSlot.Right)
            {
                WorkspaceSurfaceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                WorkspaceSurfaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                WorkspaceSurfaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                Grid.SetRow(TargetViewport, 0);
                Grid.SetRow(MacroEditorHost, 0);
                Grid.SetColumn(TargetViewport, editorSlot == DockSlot.Left ? 1 : 0);
                Grid.SetColumn(MacroEditorHost, editorSlot == DockSlot.Left ? 0 : 1);
                return;
            }

            WorkspaceSurfaceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            WorkspaceSurfaceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            WorkspaceSurfaceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid.SetColumn(TargetViewport, 0);
            Grid.SetColumn(MacroEditorHost, 0);
            Grid.SetRow(TargetViewport, editorSlot == DockSlot.Top ? 1 : 0);
            Grid.SetRow(MacroEditorHost, editorSlot == DockSlot.Top ? 0 : 1);
        }

        private static DockSlot GetOppositeDockSlot(DockSlot dockSlot)
        {
            return dockSlot switch
            {
                DockSlot.Left => DockSlot.Right,
                DockSlot.Right => DockSlot.Left,
                DockSlot.Top => DockSlot.Bottom,
                DockSlot.Bottom => DockSlot.Top,
                _ => DockSlot.Full
            };
        }

        private void RefreshWorkspaceTabs()
        {
            UpdateWorkspaceTabsChromeVisibility();
            WorkspaceTabsPanel.Children.Clear();
            EnsureDefaultGameTab();

            foreach (GameWorkspaceTab tab in _gameTabs)
            {
                WpfButton gameButton = new()
                {
                    Content = tab.TargetWindow?.ProcessName ?? tab.DisplayName,
                    Style = (Style)FindResource(tab == _activeGameTab && _isTargetWorkspaceActive
                        ? "WorkspaceTabActiveButtonStyle"
                        : "WorkspaceTabButtonStyle"),
                    Tag = tab,
                    ContextMenu = CreateTargetTabContextMenu(tab)
                };
                gameButton.Click += TargetWorkspaceTabButton_OnClick;
                WorkspaceTabsPanel.Children.Add(gameButton);
            }

            WpfButton addGameButton = new()
            {
                Width = 24,
                MinWidth = 24,
                Padding = new Thickness(0),
                Content = "\uE710",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Style = (Style)FindResource("WorkspaceTabButtonStyle"),
                ToolTip = "Add game tab"
            };
            addGameButton.Click += AddGameWorkspaceTabButton_OnClick;
            WorkspaceTabsPanel.Children.Add(addGameButton);

            Border tabDivider = new()
            {
                Width = 1,
                Height = 18,
                Margin = new Thickness(8, 5, 10, 0),
                Background = new SolidColorBrush(Color.FromArgb(72, 120, 120, 120))
            };
            WorkspaceTabsPanel.Children.Add(tabDivider);

            foreach (MacroEditorTab tab in _macroEditorTabs)
            {
                WpfButton button = new()
                {
                    Content = CreateMacroTabHeader(tab),
                    Style = (Style)FindResource(tab == _activeMacroEditorTab
                        ? "WorkspaceTabActiveButtonStyle"
                        : "WorkspaceTabButtonStyle"),
                    Tag = tab,
                    ContextMenu = CreateMacroTabContextMenu(tab)
                };
                button.Click += MacroEditorTabButton_OnClick;
                WorkspaceTabsPanel.Children.Add(button);
            }
        }

        private void UpdateWorkspaceTabsChromeVisibility()
        {
            bool showSingleTabStrip = _clientLayoutMode == ClientLayoutMode.OneByOne;
            WorkspaceTabsChrome.Visibility = showSingleTabStrip ? Visibility.Visible : Visibility.Collapsed;
            WorkspaceTabsRow.Height = showSingleTabStrip
                ? new GridLength(30)
                : new GridLength(0);
        }

        private object CreateMacroTabHeader(MacroEditorTab tab)
        {
            DockPanel panel = new()
            {
                LastChildFill = true
            };

            WpfButton closeButton = new()
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(8, 0, -6, 0),
                Padding = new Thickness(0),
                Content = "\uE8BB",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 8,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Tag = tab,
            };
            closeButton.Click += CloseMacroTabButton_OnClick;
            DockPanel.SetDock(closeButton, Dock.Right);
            panel.Children.Add(closeButton);

            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Text = tab.DisplayName + (tab.IsDirty ? " *" : string.Empty),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            return panel;
        }

        private void WorkspaceTabsScrollViewer_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer scrollViewer)
            {
                return;
            }

            scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - e.Delta);
            e.Handled = true;
        }

        private ContextMenu CreateTargetTabContextMenu(GameWorkspaceTab? tab = null)
        {
            tab ??= _activeGameTab;
            ContextMenu menu = new();
            WpfMenuItem attachItem = new() { Header = "Attach Target" };
            attachItem.Click += (_, _) =>
            {
                if (tab is not null)
                {
                    ActivateTargetWorkspace(tab);
                }

                ShowTargetPickerForActiveGameTab();
            };
            menu.Items.Add(attachItem);

            WpfMenuItem detachItem = new() { Header = "Detach Target", IsEnabled = tab?.TargetWindow is not null };
            detachItem.Click += (_, _) =>
            {
                if (tab is not null)
                {
                    DetachTargetWindow(tab);
                }
            };
            menu.Items.Add(detachItem);
            return menu;
        }

        private ContextMenu CreateMacroTabContextMenu(MacroEditorTab tab)
        {
            ContextMenu menu = new();
            menu.Items.Add(CreateContextMenuItem("Save", (_, _) => SaveEditorTab(tab)));
            menu.Items.Add(CreateContextMenuItem("Save & Close", (_, _) =>
            {
                SaveEditorTab(tab);
                CloseMacroEditorTab(tab);
            }));
            menu.Items.Add(CreateContextMenuItem("Close", (_, _) => CloseMacroEditorTab(tab)));
            return menu;
        }

        private WpfMenuItem CreateDockMenu(Action<DockSlot> onDock, DockSlot currentSlot)
        {
            WpfMenuItem dockMenu = new() { Header = "Dock" };
            dockMenu.Items.Add(CreateDockMenuItem("Full", DockSlot.Full, currentSlot, onDock));
            dockMenu.Items.Add(CreateDockMenuItem("Left", DockSlot.Left, currentSlot, onDock));
            dockMenu.Items.Add(CreateDockMenuItem("Right", DockSlot.Right, currentSlot, onDock));
            dockMenu.Items.Add(CreateDockMenuItem("Top", DockSlot.Top, currentSlot, onDock));
            dockMenu.Items.Add(CreateDockMenuItem("Bottom", DockSlot.Bottom, currentSlot, onDock));
            dockMenu.Items.Add(new Separator());
            dockMenu.Items.Add(CreateDockMenuItem("Top Left", DockSlot.TopLeft, currentSlot, onDock));
            dockMenu.Items.Add(CreateDockMenuItem("Top Right", DockSlot.TopRight, currentSlot, onDock));
            dockMenu.Items.Add(CreateDockMenuItem("Bottom Left", DockSlot.BottomLeft, currentSlot, onDock));
            dockMenu.Items.Add(CreateDockMenuItem("Bottom Right", DockSlot.BottomRight, currentSlot, onDock));
            return dockMenu;
        }

        private WpfMenuItem CreateDockMenuItem(string header, DockSlot slot, DockSlot currentSlot, Action<DockSlot> onDock)
        {
            WpfMenuItem item = new()
            {
                Header = header,
                IsCheckable = true,
                IsChecked = slot == currentSlot
            };
            item.Click += (_, _) => onDock(slot);
            return item;
        }

        private void SetMacroDockSlot(MacroEditorTab tab, DockSlot slot)
        {
            tab.DockSlot = NormalizeRenderableDockSlot(slot);
            ActivateMacroEditorTab(tab);
            SetStatus($"Docked {tab.DisplayName} {GetDockSlotLabel(tab.DockSlot)}.");
        }

        private void SetGameDockSlot(GameWorkspaceTab? tab, DockSlot slot)
        {
            if (tab is null)
            {
                return;
            }

            tab.DockSlot = NormalizeRenderableDockSlot(slot);
            ActivateTargetWorkspace(tab);
            SetStatus($"Docked {tab.DisplayName} {GetDockSlotLabel(tab.DockSlot)}.");
        }

        private static DockSlot NormalizeRenderableDockSlot(DockSlot slot)
        {
            return slot switch
            {
                DockSlot.TopLeft or DockSlot.BottomLeft => DockSlot.Left,
                DockSlot.TopRight or DockSlot.BottomRight => DockSlot.Right,
                _ => slot
            };
        }

        private static string GetDockSlotLabel(DockSlot slot)
        {
            return slot switch
            {
                DockSlot.Full => "full",
                DockSlot.Left => "left",
                DockSlot.Right => "right",
                DockSlot.Top => "top",
                DockSlot.Bottom => "bottom",
                _ => "split"
            };
        }

        private static string GetClientLayoutLabel(ClientLayoutMode mode)
        {
            return mode switch
            {
                ClientLayoutMode.TwoColumns => "2 x 1",
                ClientLayoutMode.TwoRows => "1 x 2",
                ClientLayoutMode.TwoByTwo => "2 x 2",
                ClientLayoutMode.LeftStack => "left stack",
                ClientLayoutMode.RightStack => "right stack",
                _ => "1 x 1"
            };
        }

        private void CloseMacroTabButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is WpfButton { Tag: MacroEditorTab tab })
            {
                CloseMacroEditorTab(tab);
                e.Handled = true;
            }
        }

        private void MacroEditorTabButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is WpfButton { Tag: MacroEditorTab tab })
            {
                ActivateMacroEditorTab(tab);
            }
        }

        private void MacroEditorTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingEditorText || _activeMacroEditorTab is null)
            {
                return;
            }

            _activeMacroEditorTab.Text = MacroEditorTextBox.Text;
            _activeMacroEditorTab.IsDirty = true;
            RefreshWorkspaceTabs();
            if (_clientLayoutMode != ClientLayoutMode.OneByOne && _macroEditorTabs.Count > 1)
            {
                ApplyWorkspaceLayout();
            }
        }

        private void SaveActiveEditorTab()
        {
            if (_activeMacroEditorTab is null)
            {
                return;
            }

            SaveEditorTab(_activeMacroEditorTab);
            SetStatus("Macro editor saved.");
        }

        private void SaveEditorTab(MacroEditorTab tab)
        {
            _macroLibraryService.SaveMacroText(tab.FilePath, tab.Text);
            tab.IsDirty = false;
            SetCurrentMacroText(tab.FilePath);
            RefreshWorkspaceTabs();
            RefreshMacroTree();
        }

        private void CloseMacroEditorTab(MacroEditorTab tab)
        {
            int tabIndex = _macroEditorTabs.IndexOf(tab);
            _macroEditorTabs.Remove(tab);

            if (_activeMacroEditorTab != tab)
            {
                RefreshWorkspaceTabs();
                return;
            }

            MacroEditorTab? nextTab = _macroEditorTabs.Count == 0
                ? null
                : _macroEditorTabs[Math.Clamp(tabIndex, 0, _macroEditorTabs.Count - 1)];

            if (nextTab is null)
            {
                ActivateTargetWorkspace();
            }
            else
            {
                ActivateMacroEditorTab(nextTab);
            }
        }

        #endregion

        #region Recording And Playback State

        private void StartRecordingButton_OnClick(object sender, RoutedEventArgs e)
        {
            StartRecording();
        }

        private void RecordButton_OnClick(object sender, RoutedEventArgs e)
        {
            StartRecording();
        }

        private void StartRecording()
        {
            if (_isPlaying)
            {
                return;
            }

            if (_currentMacroPath is null)
            {
                SetStatus("Load or create a macro before recording.");
                return;
            }

            List<WindowInfo> targets = GetAttachedTargetWindows();
            if (targets.Count == 0)
            {
                SetStatus("Attach at least one target before recording.");
                return;
            }

            _isRecording = true;
            _recordingStartedAt = DateTimeOffset.Now;
            _macroRecorderService.Start(targets);
            _recordingTimer.Start();
            UpdateRecordingUi();
            UpdatePlaybackUi();
            SetStatus($"Recording to {Path.GetFileName(_currentMacroPath)}.");
        }

        private void StopRecordingButton_OnClick(object sender, RoutedEventArgs e)
        {
            StopRecording();
        }

        private void StopRecording()
        {
            if (!_isRecording)
            {
                return;
            }

            _isRecording = false;
            _recordingTimer.Stop();
            if (_currentMacroPath is not null)
            {
                MacroDocument document = _macroRecorderService.StopToDocument(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(_currentMacroPath)));
                _macroLibraryService.SaveMacroDocument(_currentMacroPath, document);
                MacroEditorTab? tab = _macroEditorTabs.FirstOrDefault(item => string.Equals(item.FilePath, _currentMacroPath, StringComparison.OrdinalIgnoreCase));
                if (tab is not null)
                {
                    tab.Text = _macroLibraryService.ReadMacroText(_currentMacroPath);
                    tab.IsDirty = false;
                    if (tab == _activeMacroEditorTab)
                    {
                        _isUpdatingEditorText = true;
                        MacroEditorTextBox.Text = tab.Text;
                        _isUpdatingEditorText = false;
                    }
                }
            }
            else
            {
                _macroRecorderService.Stop();
            }
            UpdateRecordingUi();
            UpdatePlaybackUi();
            SetStatus("Recording stopped.");
        }

        private async void PlayButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (_isRecording)
            {
                return;
            }

            if (_currentMacroPath is null)
            {
                SetStatus("Open or save a macro before playback.");
                return;
            }

            List<WindowInfo> targets = GetAttachedTargetWindows();
            if (targets.Count == 0)
            {
                SetStatus("Attach at least one target before playback.");
                return;
            }

            MacroDocument document = _macroLibraryService.ReadMacroDocument(_currentMacroPath);
            if (document.Actions.Count == 0)
            {
                SetStatus("Selected macro has no recorded actions.");
                return;
            }

            _playbackCancellation?.Cancel();
            _playbackCancellation = new CancellationTokenSource();
            _isPlaying = true;
            UpdatePlaybackUi();
            UpdateRecordingUi();
            SetStatus($"Playing {document.Actions.Count} actions on {targets.Count} target(s).");

            try
            {
                await _macroPlaybackService.PlayAsync(document, targets, _playbackSpeed, _playbackCancellation.Token);
                if (_isPlaying)
                {
                    StopPlayback("Playback finished.");
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void PauseButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (!_isPlaying)
            {
                return;
            }

            StopPlayback("Playback paused.");
        }

        private void StopPlaybackButton_OnClick(object sender, RoutedEventArgs e)
        {
            StopPlayback();
        }

        private void StopButton_OnClick(object sender, RoutedEventArgs e)
        {
            bool wasBusy = _isRecording || _isPlaying;
            StopRecording();
            StopPlayback();

            if (!wasBusy)
            {
                SetStatus("Nothing is currently running.");
            }
        }

        private void StopPlayback()
        {
            StopPlayback("Playback stopped.");
        }

        private void StopPlayback(string statusMessage)
        {
            if (!_isPlaying)
            {
                return;
            }

            _isPlaying = false;
            _playbackCancellation?.Cancel();
            _playbackCancellation = null;
            UpdatePlaybackUi();
            UpdateRecordingUi();
            SetStatus(statusMessage);
        }

        private void PlaybackSpeedSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            SetPlaybackSpeed(e.NewValue);
        }

        private void PlaybackSpeedMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is not WpfMenuItem { Tag: string tag }
                || !TryParsePlaybackSpeed(tag, out double speed))
            {
                return;
            }

            SetPlaybackSpeed(speed);
            SetStatus($"Playback speed set to {_playbackSpeed:0.###}x.");
        }

        private void CustomPlaybackSpeedMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            string? input = AskForText("Playback Speed", "Input playback speed (IE: 1, 1.5, 2.25):", $"{_playbackSpeed:0.###}");
            if (string.IsNullOrWhiteSpace(input))
            {
                return;
            }

            if (!TryParsePlaybackSpeed(input, out double speed))
            {
                SetStatus("Playback speed must be a positive number.");
                return;
            }

            SetPlaybackSpeed(speed);
            SetStatus($"Playback speed set to {_playbackSpeed:0.###}x.");
        }

        private static bool TryParsePlaybackSpeed(string value, out double speed)
        {
            return (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out speed)
                    || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out speed))
                   && speed > 0;
        }

        private List<WindowInfo> GetAttachedTargetWindows()
        {
            return _gameTabs
                .Where(tab => tab.TargetWindow is not null && _windowEnumerationService.IsWindowAlive(tab.TargetWindow))
                .Select(tab => tab.TargetWindow!)
                .GroupBy(window => window.Handle)
                .Select(group => group.First())
                .ToList();
        }

        private void RecordingTimer_OnTick(object? sender, EventArgs e)
        {
            if (_recordingStartedAt is null)
            {
                return;
            }

            TimeSpan elapsed = DateTimeOffset.Now - _recordingStartedAt.Value;
            SetStatus($"Recording... {elapsed:hh\\:mm\\:ss}. {_macroRecorderService.Actions.Count} actions.");
        }

        private void UpdateRecordingUi()
        {
            bool canStartRecording = !_isRecording && !_isPlaying;

            RecordButton.IsEnabled = canStartRecording;
            RecordButton.Opacity = _isPlaying ? 0.56 : 1;

            if (_isRecording)
            {
                RecordIcon.BeginAnimation(OpacityProperty, new DoubleAnimation
                {
                    From = 0.35,
                    To = 1,
                    Duration = TimeSpan.FromMilliseconds(430),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                });
            }
            else
            {
                RecordIcon.BeginAnimation(OpacityProperty, null);
                RecordIcon.Opacity = _isPlaying ? 0.34 : 1;
            }

            SetActionButtonVisualState(StopButton, StopIcon, _isRecording || _isPlaying);
        }

        private void UpdatePlaybackUi()
        {
            SetActionButtonVisualState(PlayButton, PlayIcon, !_isPlaying && !_isRecording);
            SetActionButtonVisualState(StopButton, StopIcon, _isRecording || _isPlaying);
        }

        private static void SetActionButtonVisualState(WpfButton button, WpfShape icon, bool isActive)
        {
            button.IsEnabled = isActive;
            button.Opacity = isActive ? 1.0 : 0.56;
            icon.Opacity = isActive ? 1.0 : 0.34;
        }

        private void UpdatePlaybackSpeedText()
        {
            if (PlaybackSpeedText is not null)
            {
                PlaybackSpeedText.Text = $"Speed: {_playbackSpeed:0.###}x";
            }
        }

        private void SetPlaybackSpeed(double speed)
        {
            _playbackSpeed = Math.Clamp(speed, 0.01, 100);
            UpdatePlaybackSpeedText();
        }

        #endregion

        #region Window Hole Region

        private void MainWindow_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateWindowHoleRegion();
            ScheduleTargetSync();
        }

        private void MainWindow_OnActivated(object? sender, EventArgs e)
        {
            BringTargetForwardForOverlay();
        }

        private void MainWindow_OnLocationChanged(object? sender, EventArgs e)
        {
            if (_isDraggingTitleBar)
            {
                ResizeAttachedTargetsToViewport(includeActiveTarget: true);
                UpdateWindowHoleRegion();
                return;
            }

            ClampTitleBarToCurrentMonitorVertically();
            ScheduleTargetSync();
        }

        private void MainWindow_OnStateChanged(object? sender, EventArgs e)
        {
            UpdateMaximizeButtonIcon();
            UpdateWindowHoleRegion();
            ScheduleTargetSync();
        }

        private void TargetViewport_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateWindowHoleRegion();
            ScheduleTargetSync();
        }

        private void CenterAcrylicToggle_OnChanged(object sender, RoutedEventArgs e)
        {
            UpdateWindowHoleRegion();
        }

        private void UpdateWindowHoleRegion()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            WindowBackdropType = WindowBackdropType.None;

            if (!_isTargetWorkspaceActive)
            {
                CenterAcrylicSurface.Visibility = Visibility.Hidden;
                ClearWindowHoleRegion(hwnd);
                return;
            }

            if (CenterAcrylicToggle?.IsChecked == true)
            {
                CenterAcrylicSurface.Visibility = Visibility.Visible;
                ClearWindowHoleRegion(hwnd);
                return;
            }

            CenterAcrylicSurface.Visibility = Visibility.Hidden;

            List<FrameworkElement> holeElements = GetTargetHoleElements();
            if (holeElements.Count == 0)
            {
                return;
            }

            HwndSource? source = HwndSource.FromHwnd(hwnd);
            if (source?.CompositionTarget is null)
            {
                return;
            }

            Matrix transform = source.CompositionTarget.TransformToDevice;
            Point windowSize = transform.Transform(new Point(ActualWidth, ActualHeight));

            IntPtr windowRegion = CreateRoundRectRgn(
                0,
                0,
                (int)Math.Ceiling(windowSize.X),
                (int)Math.Ceiling(windowSize.Y),
                20,
                20);

            IntPtr combinedRegion = CreateRectRgn(0, 0, 0, 0);
            CombineRgn(combinedRegion, windowRegion, windowRegion, 5);

            foreach (FrameworkElement holeElement in holeElements)
            {
                Point holeTopLeft = transform.Transform(holeElement.TransformToAncestor(this).Transform(new Point(0, 0)));
                Point holeBottomRight = transform.Transform(holeElement.TransformToAncestor(this).Transform(new Point(holeElement.ActualWidth, holeElement.ActualHeight)));

                IntPtr holeRegion = CreateRoundRectRgn(
                    (int)Math.Floor(holeTopLeft.X),
                    (int)Math.Floor(holeTopLeft.Y),
                    (int)Math.Ceiling(holeBottomRight.X),
                    (int)Math.Ceiling(holeBottomRight.Y),
                    20,
                    20);

                IntPtr nextRegion = CreateRectRgn(0, 0, 0, 0);
                CombineRgn(nextRegion, combinedRegion, holeRegion, RegionDifference);
                DeleteObject(combinedRegion);
                DeleteObject(holeRegion);
                combinedRegion = nextRegion;
            }

            if (SetWindowRgn(hwnd, combinedRegion, true) == 0)
            {
                DeleteObject(combinedRegion);
            }

            DeleteObject(windowRegion);
        }

        private List<FrameworkElement> GetTargetHoleElements()
        {
            if (_gamePaneTargets.Count > 0)
            {
                return _gamePaneTargets
                    .Where(pair => HasLiveTarget(pair.Key)
                                   && pair.Value.ActualWidth > 0
                                   && pair.Value.ActualHeight > 0)
                    .Select(pair => (FrameworkElement)pair.Value)
                    .ToList();
            }

            return TargetViewport.Visibility == Visibility.Visible
                   && TargetViewport.ActualWidth > 0
                   && TargetViewport.ActualHeight > 0
                ? [TargetViewport]
                : [];
        }

        private static void ClearWindowHoleRegion(IntPtr hwnd)
        {
            SetWindowRgn(hwnd, IntPtr.Zero, true);
        }

        private void SyncTargetWindowToViewport()
        {
            if (_gamePaneTargets.Count > 0)
            {
                ResizeGamePaneTargets();
                UpdateWindowHoleRegion();
                return;
            }

            if (_isDraggingTitleBar)
            {
                ResizeAttachedTargetsToViewport(includeActiveTarget: true);
                UpdateWindowHoleRegion();
                return;
            }

            if (_selectedTargetWindow is null)
            {
                ResizeAttachedTargetsToViewport(includeActiveTarget: false);
                return;
            }

            if (!_isTargetWorkspaceActive)
            {
                ResizeTargetToViewport(_selectedTargetWindow);
                UpdateWindowHoleRegion();
                return;
            }

            ResizeTargetToViewport(_selectedTargetWindow);
            ResizeAttachedTargetsToViewport(includeActiveTarget: false);
            UpdateWindowHoleRegion();
        }

        private void ResizeAttachedTargetsToViewport(bool includeActiveTarget)
        {
            HashSet<nint> resizedHandles = [];

            if (_gamePaneTargets.Count > 0)
            {
                ResizeGamePaneTargets();
                return;
            }

            foreach (GameWorkspaceTab tab in _gameTabs)
            {
                if (tab.TargetWindow is null)
                {
                    continue;
                }

                if (!includeActiveTarget
                    && _selectedTargetWindow is not null
                    && tab.TargetWindow.Handle == _selectedTargetWindow.Handle)
                {
                    continue;
                }

                if (!resizedHandles.Add(tab.TargetWindow.Handle))
                {
                    continue;
                }

                ResizeTargetToViewport(tab.TargetWindow);
            }
        }

        private void ResizeGamePaneTargets()
        {
            HashSet<nint> resizedHandles = [];

            foreach ((GameWorkspaceTab tab, FrameworkElement host) in _gamePaneTargets)
            {
                if (!HasLiveTarget(tab)
                    || host.ActualWidth <= 0
                    || host.ActualHeight <= 0
                    || tab.TargetWindow is null
                    || !resizedHandles.Add(tab.TargetWindow.Handle))
                {
                    continue;
                }

                ResizeTargetToElement(tab.TargetWindow, host);
            }
        }

        private void ScheduleTargetSync()
        {
            if (_isTargetSyncPaused || _isDraggingTitleBar)
            {
                return;
            }

            _targetSyncUntil = DateTimeOffset.Now.AddMilliseconds(350);
            SyncTargetWindowToViewport();

            if (!_targetSyncTimer.IsEnabled)
            {
                _targetSyncTimer.Start();
            }
        }

        private void TargetSyncTimer_OnTick(object? sender, EventArgs e)
        {
            if (_isDraggingTitleBar)
            {
                return;
            }

            if (_targetSyncUntil is null || DateTimeOffset.Now > _targetSyncUntil)
            {
                _targetSyncTimer.Stop();
                _targetSyncUntil = null;
                return;
            }

            SyncTargetWindowToViewport();
            UpdateCursorText();
        }

        #endregion

        #region Click-Through Hit Testing

        private void MainWindow_OnSourceInitialized(object? sender, EventArgs e)
        {
            _mainHwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _mainHwndSource?.AddHook(WindowMessageHook);
            RegisterAutoclickerHotkey();
            RegisterAntiAfkHotkey();
            RegisterBlackoutHotkey();
            UpdateWindowHoleRegion();
        }

        private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int wmNchittest = 0x0084;
            const int htTransparent = -1;

            if (message == WmHotkey && wParam.ToInt32() == AutoclickerHotkeyId)
            {
                HandleAutoclickerHotkeyPressed(false);
                handled = true;
                return IntPtr.Zero;
            }

            if (message == WmHotkey && wParam.ToInt32() == AntiAfkHotkeyId)
            {
                ToggleAntiAfk();
                handled = true;
                return IntPtr.Zero;
            }

            if (message == WmHotkey && wParam.ToInt32() == BlackoutToggleHotkeyId)
            {
                ToggleBlackoutOverlay();
                handled = true;
                return IntPtr.Zero;
            }

            if (message != wmNchittest
                || CenterAcrylicToggle?.IsChecked == true
                || !IsPointInsideTargetViewport(lParam))
            {
                return IntPtr.Zero;
            }

            // Let the target window below receive clicks inside the preview area.
            handled = true;
            return new IntPtr(htTransparent);
        }

        private bool IsPointInsideTargetViewport(IntPtr lParam)
        {
            int x = unchecked((short)((long)lParam & 0xFFFF));
            int y = unchecked((short)(((long)lParam >> 16) & 0xFFFF));

            foreach (FrameworkElement element in GetTargetHoleElements())
            {
                Point topLeft = element.PointToScreen(new Point(0, 0));
                Point bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));

                if (x >= topLeft.X
                    && x <= bottomRight.X
                    && y >= topLeft.Y
                    && y <= bottomRight.Y)
                {
                    return true;
                }
            }

            return false;
        }

        private void RegisterAutoclickerHotkey()
        {
            if (_mainHwndSource is null)
            {
                return;
            }

            UnregisterAutoclickerHotkey();

            if (string.IsNullOrWhiteSpace(_autoclickerHotkey)
                || IsMouseButtonHotkey(_autoclickerHotkey)
                || !Enum.TryParse(_autoclickerHotkey, true, out Key key))
            {
                return;
            }

            int virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey <= 0)
            {
                return;
            }

            RegisterHotKey(_mainHwndSource.Handle, AutoclickerHotkeyId, 0, (uint)virtualKey);
        }

        private static bool IsMouseButtonHotkey(string hotkey)
        {
            return string.Equals(hotkey, nameof(MouseButton.Left), StringComparison.OrdinalIgnoreCase)
                   || string.Equals(hotkey, nameof(MouseButton.Middle), StringComparison.OrdinalIgnoreCase)
                   || string.Equals(hotkey, nameof(MouseButton.Right), StringComparison.OrdinalIgnoreCase)
                   || string.Equals(hotkey, nameof(MouseButton.XButton1), StringComparison.OrdinalIgnoreCase)
                   || string.Equals(hotkey, nameof(MouseButton.XButton2), StringComparison.OrdinalIgnoreCase);
        }

        private void UnregisterAutoclickerHotkey()
        {
            if (_mainHwndSource is null)
            {
                return;
            }

            UnregisterHotKey(_mainHwndSource.Handle, AutoclickerHotkeyId);
        }

        private void RegisterAntiAfkHotkey()
        {
            if (_mainHwndSource is null)
            {
                return;
            }

            UnregisterAntiAfkHotkey();

            if (string.IsNullOrWhiteSpace(_antiAfkHotkey)
                || IsMouseButtonHotkey(_antiAfkHotkey)
                || !Enum.TryParse(_antiAfkHotkey, true, out Key key))
            {
                return;
            }

            int virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey <= 0)
            {
                return;
            }

            RegisterHotKey(_mainHwndSource.Handle, AntiAfkHotkeyId, 0, (uint)virtualKey);
        }

        private void UnregisterAntiAfkHotkey()
        {
            if (_mainHwndSource is null)
            {
                return;
            }

            UnregisterHotKey(_mainHwndSource.Handle, AntiAfkHotkeyId);
        }

        private void RegisterBlackoutHotkey()
        {
            if (_mainHwndSource is null)
            {
                return;
            }

            UnregisterBlackoutHotkey();
            RegisterHotKey(
                _mainHwndSource.Handle,
                BlackoutToggleHotkeyId,
                ModControl | /*ModAlt |*/ ModNoRepeat,
                (uint)KeyInterop.VirtualKeyFromKey(Key.B));
        }

        private void UnregisterBlackoutHotkey()
        {
            if (_mainHwndSource is null)
            {
                return;
            }

            UnregisterHotKey(_mainHwndSource.Handle, BlackoutToggleHotkeyId);
        }

        #endregion

        #region Status

        private void ClockTimer_OnTick(object? sender, EventArgs e)
        {
            ClockText.Text = DateTime.Now.ToLongTimeString();
            DetachClosedTargets();
            UpdateActiveGameTabFromForegroundWindow();
        }

        private void DetachClosedTargets()
        {
            bool changed = false;

            foreach (GameWorkspaceTab tab in _gameTabs)
            {
                if (tab.TargetWindow is not null && !_windowEnumerationService.IsWindowAlive(tab.TargetWindow))
                {
                    tab.TargetWindow = null;
                    tab.DisplayName = $"Game {tab.Id}";
                    tab.IsRobloxTarget = false;
                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }

            if (_activeGameTab?.TargetWindow is null)
            {
                SyncActiveGameTabToWindowState();
                TitleTargetStatusText.Text = "Target not attached";
                SetTargetLinkedState(isLinked: false);
            }

            ApplyWorkspaceLayout();
            UpdateTargetIndicator();
            RefreshWorkspaceTabs();
        }

        private void UpdateActiveGameTabFromForegroundWindow()
        {
            nint foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == nint.Zero)
            {
                return;
            }

            GameWorkspaceTab? focusedTab = _gameTabs.FirstOrDefault(tab => tab.TargetWindow?.Handle == foregroundWindow);
            if (focusedTab is null || focusedTab == _activeGameTab)
            {
                return;
            }

            _activeGameTab = focusedTab;
            SyncActiveGameTabToWindowState();
            TitleTargetStatusText.Text = focusedTab.TargetWindow?.DisplayName ?? "Target not attached";
            UpdateTargetIndicator();
            RefreshWorkspaceTabs();
        }

        private bool HasLiveTarget(GameWorkspaceTab tab)
        {
            return tab.TargetWindow is not null && _windowEnumerationService.IsWindowAlive(tab.TargetWindow);
        }

        private void CursorRendering_OnRendering(object? sender, EventArgs e)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            if (nowTicks - _lastCursorTextUpdateTicks < Stopwatch.Frequency * CursorTextUpdateMilliseconds / 1000)
            {
                return;
            }

            _lastCursorTextUpdateTicks = nowTicks;
            UpdateCursorText();
        }

        private void UpdateCursorText()
        {
            if (!TryGetCursorTargetUnderCursor(out GameWorkspaceTab? tab, out WindowClientPoint position))
            {
                SetCursorTextIfChanged("CORD: (--, --)");
                return;
            }

            SetCursorTextIfChanged($"{tab!.DisplayName}: ({position.X}, {position.Y})");
        }

        private void SetCursorTextIfChanged(string text)
        {
            if (string.Equals(_lastCursorText, text, StringComparison.Ordinal))
            {
                return;
            }

            _lastCursorText = text;
            CursorText.Text = text;
        }

        private bool TryGetCursorTargetUnderCursor(out GameWorkspaceTab? targetTab, out WindowClientPoint position)
        {
            foreach (GameWorkspaceTab tab in _gameTabs)
            {
                if (tab.TargetWindow is null)
                {
                    continue;
                }

                if (_windowEnumerationService.TryGetCursorClientPosition(tab.TargetWindow, out position))
                {
                    targetTab = tab;
                    return true;
                }
            }

            targetTab = null;
            position = default;
            return false;
        }

        private void SetStatus(string message)
        {
            StatusText.Text = $"Status: {message}";
        }

        #endregion

        #region Native Window Region

        private const int RegionDifference = 4;

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

        [DllImport("gdi32.dll")]
        private static extern int CombineRgn(IntPtr destinationRegion, IntPtr sourceRegion1, IntPtr sourceRegion2, int combineMode);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(nint hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo monitorInfo);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeInput
        {
            public uint Type;
            public NativeMouseInput Mouse;
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
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect WorkArea;
            public uint Flags;
        }

        #endregion

        #region Local Models

        private readonly record struct TargetResolutionMode(string DisplayName, int Width, int Height)
        {
            public bool HasFixedSize => Width > 0 && Height > 0;

            public static TargetResolutionMode Follow => new("Follow Target", 0, 0);

            public static TargetResolutionMode FromTag(string tag)
            {
                return tag switch
                {
                    "1920x1080" => new TargetResolutionMode("1920 x 1080", 1920, 1080),
                    "2560x1440" => new TargetResolutionMode("2560 x 1440", 2560, 1440),
                    _ => Follow
                };
            }
        }

        private readonly record struct PanePlacement(int Row, int Column, int RowSpan = 1, int ColumnSpan = 1);

        private sealed class MacroEditorTab(string filePath, string displayName, string text)
        {
            public string FilePath { get; } = filePath;

            public string DisplayName { get; } = displayName;

            public string Text { get; set; } = text;

            public bool IsDirty { get; set; }

            public DockSlot DockSlot { get; set; } = DockSlot.Full;
        }

        private sealed class GameWorkspaceTab(int id, string displayName)
        {
            public int Id { get; } = id;

            public string DisplayName { get; set; } = displayName;

            public WindowInfo? TargetWindow { get; set; }

            public bool IsRobloxTarget { get; set; }

            public TargetResolutionMode ResolutionMode { get; set; } = TargetResolutionMode.Follow;

            public DockSlot DockSlot { get; set; } = DockSlot.Full;
        }

        private enum DockSlot
        {
            Full,
            Left,
            Right,
            Top,
            Bottom,
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight
        }

        private enum ClientLayoutMode
        {
            OneByOne,
            TwoColumns,
            TwoRows,
            TwoByTwo,
            LeftStack,
            RightStack
        }

        #endregion
    }
}
