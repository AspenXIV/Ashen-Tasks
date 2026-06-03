using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AshenTasker.Configuration;
using AshenTasker.Services.Storage;
using Microsoft.Win32;

namespace AshenTasker.Views.Dialogs;

public partial class SettingsWindow : Window
{
    #region Fields And Properties

    private readonly MacroLibraryService _macroLibraryService = new();
    private Button? _capturingHotkeyButton;

    #endregion

    #region Construction

    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += SettingsWindow_OnLoaded;
        PreviewKeyDown += SettingsWindow_OnPreviewKeyDown;
        PreviewMouseDown += SettingsWindow_OnPreviewMouseDown;
    }

    private void SettingsWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        MacroDirectoryTextBox.Text = _macroLibraryService.RootDirectory;
        HideProcessIdsCheckBox.IsChecked = AppSettings.HideProcessIds;
        PauseWhenTargetChangesCheckBox.IsChecked = AppSettings.PauseWhenTargetChanges;
        ToggleOverlayHotkeyButton.Content = AppSettings.ToggleOverlayHotkey;
        RecordHotkeyButton.Content = AppSettings.RecordHotkey;
        PlayHotkeyButton.Content = AppSettings.PlayHotkey;
        StopHotkeyButton.Content = AppSettings.StopHotkey;
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
        Close();
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    #endregion

    #region Page Navigation

    private void SettingsTab_OnChecked(object sender, RoutedEventArgs e)
    {
        if (InfoPage is null || SettingsPage is null)
        {
            return;
        }

        bool showInfo = InfoTabButton.IsChecked == true;
        InfoPage.Visibility = showInfo ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = showInfo ? Visibility.Collapsed : Visibility.Visible;
    }

    #endregion

    #region Settings Actions

    private void BrowseMacroDirectoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Select Macro Directory",
            InitialDirectory = MacroDirectoryTextBox.Text
        };

        if (dialog.ShowDialog(this) == true)
        {
            MacroDirectoryTextBox.Text = dialog.FolderName;
            AppSettings.MacroDirectory = dialog.FolderName;
            AppSettings.Save();
        }
    }

    private void HideProcessIdsCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        AppSettings.HideProcessIds = HideProcessIdsCheckBox.IsChecked == true;
        AppSettings.Save();
    }

    private void PauseWhenTargetChangesCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        AppSettings.PauseWhenTargetChanges = PauseWhenTargetChangesCheckBox.IsChecked == true;
        AppSettings.Save();
    }

    #endregion

    #region Hotkey Capture

    private void HotkeyCaptureButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        _capturingHotkeyButton = button;
        button.Content = "...";
        button.Focus();
    }

    private void SettingsWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingHotkeyButton is null)
        {
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        SetCapturedHotkey(GetHotkeyText(key));
        e.Handled = true;
    }

    private void SettingsWindow_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_capturingHotkeyButton is null)
        {
            return;
        }

        SetCapturedHotkey(e.ChangedButton.ToString());
        e.Handled = true;
    }

    private void SetCapturedHotkey(string hotkey)
    {
        _capturingHotkeyButton!.Content = hotkey;
        SaveCapturedHotkey(_capturingHotkeyButton, hotkey);
        _capturingHotkeyButton = null;
    }

    private static string GetHotkeyText(Key key)
    {
        if (key == Key.None)
        {
            return "#";
        }

        List<string> parts = [];
        ModifierKeys modifiers = Keyboard.Modifiers;

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private static void SaveCapturedHotkey(Button button, string hotkey)
    {
        if (button.Name == nameof(ToggleOverlayHotkeyButton))
        {
            AppSettings.ToggleOverlayHotkey = hotkey;
        }
        else if (button.Name == nameof(RecordHotkeyButton))
        {
            AppSettings.RecordHotkey = hotkey;
        }
        else if (button.Name == nameof(PlayHotkeyButton))
        {
            AppSettings.PlayHotkey = hotkey;
        }
        else if (button.Name == nameof(StopHotkeyButton))
        {
            AppSettings.StopHotkey = hotkey;
        }

        AppSettings.Save();
    }

    #endregion
}
