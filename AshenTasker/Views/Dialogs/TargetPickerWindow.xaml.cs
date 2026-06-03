using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AshenTasker.Configuration;
using AshenTasker.Models.Windowing;
using AshenTasker.Services.Windowing;

namespace AshenTasker.Views.Dialogs;

public partial class TargetPickerWindow : Window
{
    #region Fields And Properties

    private readonly nint _ownerHandle;
    private readonly WindowEnumerationService _windowEnumerationService = new();
    private readonly DispatcherTimer _metricsTimer = new();
    private readonly Dictionary<int, ProcessMetricSample> _metricSamples = [];
    private readonly List<TargetProcessRow> _processRows = [];
    private ProcessColumnWidths _columnWidths = new(220, 76, 80, 120, 110);

    public IReadOnlySet<nint> AttachedTargetHandles { get; init; } = new HashSet<nint>();

    public WindowInfo? SelectedWindow { get; private set; }

    #endregion

    #region Construction

    public TargetPickerWindow(nint ownerHandle)
    {
        InitializeComponent();
        _ownerHandle = ownerHandle;

        Loaded += (_, _) =>
        {
            CaptureColumnWidths();
            RefreshTargets();
            _metricsTimer.Start();
        };
        Closed += (_, _) => _metricsTimer.Stop();

        _metricsTimer.Interval = TimeSpan.FromSeconds(1);
        _metricsTimer.Tick += (_, _) => UpdateProcessMetrics();
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

        DragMove();
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    #endregion

    #region Column Sizing

    private void ColumnSplitter_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        CaptureColumnWidths();
        UpdateVisibleTargetRows();
    }

    private void ColumnSplitter_OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        CaptureColumnWidths();
        UpdateVisibleTargetRows();
    }

    private void CaptureColumnWidths()
    {
        _columnWidths = new ProcessColumnWidths(
            Math.Max(HeaderNameColumn.MinWidth, HeaderNameColumn.ActualWidth),
            Math.Max(HeaderPidColumn.MinWidth, HeaderPidColumn.ActualWidth),
            Math.Max(HeaderCpuColumn.MinWidth, HeaderCpuColumn.ActualWidth),
            Math.Max(HeaderIoColumn.MinWidth, HeaderIoColumn.ActualWidth),
            Math.Max(HeaderPrivateBytesColumn.MinWidth, HeaderPrivateBytesColumn.ActualWidth));
    }

    #endregion

    #region Target Selection

    private void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        RefreshTargets();
    }

    private void TargetTreeView_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        SelectedWindow = (TargetTreeView.SelectedItem as TreeViewItem)?.Tag switch
        {
            WindowInfo window => window,
            TargetProcessRow { Windows.Count: 1 } row => row.Windows[0],
            _ => null
        };

        UpdateDetails();
    }

    private void LinkButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedWindow is null)
        {
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void RefreshTargets()
    {
        TargetTreeView.Items.Clear();
        _processRows.Clear();

        List<WindowInfo> windows = _windowEnumerationService.GetVisibleWindows()
            .Where(window => window.Handle != _ownerHandle)
            .Where(window => !IsBlockedTargetWindow(window))
            .ToList();

        foreach (IGrouping<int, WindowInfo> group in windows.GroupBy(window => window.ProcessId).OrderBy(group => group.First().ProcessName))
        {
            List<WindowInfo> processWindows = group.OrderBy(window => window.Title).ToList();
            TargetProcessRow row = new(
                group.First().ProcessName,
                group.Key,
                processWindows,
                processWindows.Any(IsAttachedTarget));
            _processRows.Add(row);

            TreeViewItem processItem = new()
            {
                Header = CreateProcessHeader(row, _columnWidths),
                Tag = row,
                IsExpanded = false,
                Background = row.HasAttachedTarget
                    ? new SolidColorBrush(Color.FromArgb(44, 226, 194, 92))
                    : Brushes.Transparent
            };
            row.TreeItem = processItem;

            foreach (WindowInfo window in row.Windows)
            {
                bool isAttached = IsAttachedTarget(window);
                processItem.Items.Add(new TreeViewItem
                {
                    Header = CreateWindowHeader(window, _columnWidths, isAttached),
                    Tag = window,
                    Background = isAttached
                        ? new SolidColorBrush(Color.FromArgb(72, 226, 194, 92))
                        : Brushes.Transparent
                });
            }

            TargetTreeView.Items.Add(processItem);
        }

        UpdateProcessMetrics();
    }

    private void UpdateVisibleTargetRows()
    {
        foreach (TargetProcessRow row in _processRows)
        {
            if (row.TreeItem is null)
            {
                continue;
            }

            row.IsExpanded = row.TreeItem.IsExpanded;
            row.TreeItem.Header = CreateProcessHeader(row, _columnWidths);
            row.TreeItem.Background = row.HasAttachedTarget
                ? new SolidColorBrush(Color.FromArgb(44, 226, 194, 92))
                : Brushes.Transparent;

            for (int index = 0; index < row.TreeItem.Items.Count && index < row.Windows.Count; index++)
            {
                if (row.TreeItem.Items[index] is TreeViewItem windowItem)
                {
                    bool isAttached = IsAttachedTarget(row.Windows[index]);
                    windowItem.Header = CreateWindowHeader(row.Windows[index], _columnWidths, isAttached);
                    windowItem.Background = isAttached
                        ? new SolidColorBrush(Color.FromArgb(72, 226, 194, 92))
                        : Brushes.Transparent;
                }
            }
        }
    }

    private void UpdateDetails()
    {
        LinkButton.IsEnabled = SelectedWindow is not null;

        if (SelectedWindow is null)
        {
            ProcessText.Text = "-";
            WindowText.Text = "-";
            PidText.Text = "-";
            BoundsText.Text = "-";
            return;
        }

        ProcessText.Text = SelectedWindow.ProcessName;
        WindowText.Text = SelectedWindow.Title;
        PidText.Text = FormatProcessId(SelectedWindow.ProcessId);
        BoundsText.Text = $"{SelectedWindow.Width} x {SelectedWindow.Height}";
    }

    private static bool IsBlockedTargetWindow(WindowInfo window)
    {
        if (window.ProcessId == Environment.ProcessId)
        {
            return true;
        }

        string processName = window.ProcessName;
        string title = window.Title;

        if (processName.Equals("AshenTasker", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("SystemSettings", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
               && title.Equals("Settings", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsAttachedTarget(WindowInfo window)
    {
        return AttachedTargetHandles.Contains(window.Handle);
    }

    #endregion

    #region Process Metrics

    private void UpdateProcessMetrics()
    {
        foreach (TargetProcessRow row in _processRows)
        {
            UpdateProcessMetric(row);

            if (row.TreeItem is not null)
            {
                row.IsExpanded = row.TreeItem.IsExpanded;
                row.TreeItem.Header = CreateProcessHeader(row, _columnWidths);
            }
        }
    }

    private void UpdateProcessMetric(TargetProcessRow row)
    {
        DateTime now = DateTime.UtcNow;

        try
        {
            using Process process = Process.GetProcessById(row.ProcessId);
            TimeSpan cpuTime = process.TotalProcessorTime;
            ulong ioBytes = TryGetTotalIoBytes(process.Handle, out ulong totalIoBytes) ? totalIoBytes : 0;

            row.PrivateBytesText = FormatBytes(process.PrivateMemorySize64);

            if (_metricSamples.TryGetValue(row.ProcessId, out ProcessMetricSample? previous))
            {
                double elapsedSeconds = Math.Max(0.001, (now - previous.Timestamp).TotalSeconds);
                double cpuPercent = ((cpuTime - previous.TotalCpuTime).TotalMilliseconds / (elapsedSeconds * 1000 * Environment.ProcessorCount)) * 100;

                row.CpuText = $"{Math.Max(0, cpuPercent):0.0}%";
                row.IoRateText = previous.HasIo
                    ? FormatBytesPerSecond((ioBytes - previous.TotalIoBytes) / elapsedSeconds)
                    : "--";
            }
            else
            {
                row.CpuText = "--";
                row.IoRateText = "--";
            }

            _metricSamples[row.ProcessId] = new ProcessMetricSample(now, cpuTime, ioBytes, ioBytes > 0);
        }
        catch
        {
            row.CpuText = "--";
            row.IoRateText = "--";
            row.PrivateBytesText = "--";
        }
    }

    private static string FormatBytesPerSecond(double bytesPerSecond)
    {
        return $"{FormatBytes(bytesPerSecond)}/s";
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:0} {units[unitIndex]}"
            : $"{value:0.##} {units[unitIndex]}";
    }

    #endregion

    #region Header Builders

    private static Grid CreateProcessHeader(TargetProcessRow row, ProcessColumnWidths columnWidths)
    {
        Grid grid = CreateHeaderGrid(columnWidths);
        if (row.HasAttachedTarget)
        {
            AddAttachedRowHighlight(grid);
            AddCell(grid, "●", 0, FontWeights.SemiBold, TextAlignment.Center, "#E2C25C");
        }

        AddCell(grid, row.ProcessName, 1, FontWeights.SemiBold, TextAlignment.Left);
        AddCell(grid, FormatProcessId(row.ProcessId), 3, FontWeights.Normal, TextAlignment.Right);
        AddCell(grid, row.CpuText, 5, FontWeights.Normal, TextAlignment.Right);
        AddCell(grid, row.IoRateText, 7, FontWeights.Normal, TextAlignment.Right);
        AddCell(grid, row.PrivateBytesText, 9, FontWeights.Normal, TextAlignment.Right);
        return grid;
    }

    private static Grid CreateWindowHeader(WindowInfo window, ProcessColumnWidths columnWidths, bool isAttached = false)
    {
        Grid grid = CreateHeaderGrid(columnWidths);
        if (isAttached)
        {
            AddAttachedRowHighlight(grid);
            AddCell(grid, "●", 0, FontWeights.SemiBold, TextAlignment.Center, "#E2C25C");
        }

        AddCell(grid, window.Title, 1, isAttached ? FontWeights.SemiBold : FontWeights.Normal, TextAlignment.Left, isAttached ? "#F5E2A4" : "#DDDDDD");
        AddCell(grid, FormatProcessId(window.ProcessId), 3, FontWeights.Normal, TextAlignment.Right, "#AABDBDBD");
        AddCell(grid, "-", 5, FontWeights.Normal, TextAlignment.Right, "#8A8A8A");
        AddCell(grid, "-", 7, FontWeights.Normal, TextAlignment.Right, "#8A8A8A");
        AddCell(grid, $"{window.Width} x {window.Height}", 9, FontWeights.Normal, TextAlignment.Right, "#AABDBDBD");
        return grid;
    }

    private static Grid CreateHeaderGrid(ProcessColumnWidths columnWidths)
    {
        double totalWidth = 16
                            + columnWidths.Name
                            + 4
                            + columnWidths.Pid
                            + 4
                            + columnWidths.Cpu
                            + 4
                            + columnWidths.IoRate
                            + 4
                            + columnWidths.PrivateBytes;

        Grid grid = new()
        {
            Width = Math.Max(560, totalWidth),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(columnWidths.Name) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(columnWidths.Pid) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(columnWidths.Cpu) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(columnWidths.IoRate) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(columnWidths.PrivateBytes) });

        return grid;
    }

    private static void AddAttachedRowHighlight(Grid grid)
    {
        Border highlight = new()
        {
            Background = new LinearGradientBrush(
                Color.FromArgb(70, 226, 194, 92),
                Color.FromArgb(18, 226, 194, 92),
                0),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 1, 0, 1)
        };

        Grid.SetColumnSpan(highlight, grid.ColumnDefinitions.Count);
        grid.Children.Insert(0, highlight);
    }

    private static void AddCell(Grid grid, string text, int column, FontWeight fontWeight, TextAlignment alignment, string color = "#F2F2F2")
    {
        TextBlock textBlock = new()
        {
            Margin = new Thickness(4, 2, 4, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)new BrushConverter().ConvertFromString(color)!,
            FontWeight = fontWeight,
            TextAlignment = alignment,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = text
        };

        Grid.SetColumn(textBlock, column);
        grid.Children.Add(textBlock);
    }

    private static string FormatProcessId(int processId)
    {
        return AppSettings.HideProcessIds ? "...." : processId.ToString();
    }

    #endregion

    #region Native Process IO

    private static bool TryGetTotalIoBytes(nint processHandle, out ulong totalBytes)
    {
        totalBytes = 0;

        if (!GetProcessIoCounters(processHandle, out IoCounters counters))
        {
            return false;
        }

        totalBytes = counters.ReadTransferCount + counters.WriteTransferCount + counters.OtherTransferCount;
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(nint processHandle, out IoCounters counters);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct IoCounters
    {
        public readonly ulong ReadOperationCount;
        public readonly ulong WriteOperationCount;
        public readonly ulong OtherOperationCount;
        public readonly ulong ReadTransferCount;
        public readonly ulong WriteTransferCount;
        public readonly ulong OtherTransferCount;
    }

    #endregion

    #region Local Models

    private sealed class TargetProcessRow(string processName, int processId, IReadOnlyList<WindowInfo> windows, bool hasAttachedTarget)
    {
        public string ProcessName { get; } = processName;

        public int ProcessId { get; } = processId;

        public IReadOnlyList<WindowInfo> Windows { get; } = windows;

        public bool HasAttachedTarget { get; } = hasAttachedTarget;

        public string CpuText { get; set; } = "--";

        public string IoRateText { get; set; } = "--";

        public string PrivateBytesText { get; set; } = "--";

        public bool IsExpanded { get; set; }

        public TreeViewItem? TreeItem { get; set; }
    }

    private sealed record ProcessMetricSample(DateTime Timestamp, TimeSpan TotalCpuTime, ulong TotalIoBytes, bool HasIo);

    private readonly record struct ProcessColumnWidths(double Name, double Pid, double Cpu, double IoRate, double PrivateBytes);

    #endregion
}
