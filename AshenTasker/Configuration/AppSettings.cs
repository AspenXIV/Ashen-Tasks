using System.IO;
using System.Text.Json;

namespace AshenTasker.Configuration;

public static class AppSettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AshenTasker");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string? MacroDirectory { get; set; }

    public static bool HideProcessIds { get; set; }

    public static bool PauseWhenTargetChanges { get; set; } = true;

    public static string ToggleOverlayHotkey { get; set; } = "F9";

    public static string RecordHotkey { get; set; } = "Ctrl+R";

    public static string PlayHotkey { get; set; } = "Ctrl+P";

    public static string StopHotkey { get; set; } = "Ctrl+S";

    public static double AutoclickerClicksPerSecond { get; set; } = 2;

    public static string AutoclickerHotkey { get; set; } = "F8";

    public static int AutoclickerStopAt { get; set; }

    public static string AutoclickerMode { get; set; } = "Toggle";

    public static string AutoclickerMouseButton { get; set; } = "Left";

    public static bool AutoclickerBroadcastRelativeSpot { get; set; }

    static AppSettings()
    {
        Load();
    }

    public static void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(CreateSnapshot(), JsonOptions));
    }

    private static void Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return;
        }

        try
        {
            SettingsSnapshot? snapshot = JsonSerializer.Deserialize<SettingsSnapshot>(File.ReadAllText(SettingsPath), JsonOptions);
            if (snapshot is null)
            {
                return;
            }

            MacroDirectory = snapshot.MacroDirectory;
            HideProcessIds = snapshot.HideProcessIds;
            PauseWhenTargetChanges = snapshot.PauseWhenTargetChanges;
            ToggleOverlayHotkey = ValueOrDefault(snapshot.ToggleOverlayHotkey, ToggleOverlayHotkey);
            RecordHotkey = ValueOrDefault(snapshot.RecordHotkey, RecordHotkey);
            PlayHotkey = ValueOrDefault(snapshot.PlayHotkey, PlayHotkey);
            StopHotkey = ValueOrDefault(snapshot.StopHotkey, StopHotkey);
            AutoclickerClicksPerSecond = snapshot.AutoclickerClicksPerSecond > 0 ? snapshot.AutoclickerClicksPerSecond : AutoclickerClicksPerSecond;
            AutoclickerHotkey = ValueOrDefault(snapshot.AutoclickerHotkey, AutoclickerHotkey);
            AutoclickerStopAt = Math.Max(0, snapshot.AutoclickerStopAt);
            AutoclickerMode = ValueOrDefault(snapshot.AutoclickerMode, AutoclickerMode);
            AutoclickerMouseButton = ValueOrDefault(snapshot.AutoclickerMouseButton, AutoclickerMouseButton);
            AutoclickerBroadcastRelativeSpot = snapshot.AutoclickerBroadcastRelativeSpot;
        }
        catch
        {
        }
    }

    private static SettingsSnapshot CreateSnapshot()
    {
        return new SettingsSnapshot
        {
            MacroDirectory = MacroDirectory,
            HideProcessIds = HideProcessIds,
            PauseWhenTargetChanges = PauseWhenTargetChanges,
            ToggleOverlayHotkey = ToggleOverlayHotkey,
            RecordHotkey = RecordHotkey,
            PlayHotkey = PlayHotkey,
            StopHotkey = StopHotkey,
            AutoclickerClicksPerSecond = AutoclickerClicksPerSecond,
            AutoclickerHotkey = AutoclickerHotkey,
            AutoclickerStopAt = AutoclickerStopAt,
            AutoclickerMode = AutoclickerMode,
            AutoclickerMouseButton = AutoclickerMouseButton,
            AutoclickerBroadcastRelativeSpot = AutoclickerBroadcastRelativeSpot
        };
    }

    private static string ValueOrDefault(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private sealed class SettingsSnapshot
    {
        public string? MacroDirectory { get; set; }

        public bool HideProcessIds { get; set; }

        public bool PauseWhenTargetChanges { get; set; } = true;

        public string? ToggleOverlayHotkey { get; set; }

        public string? RecordHotkey { get; set; }

        public string? PlayHotkey { get; set; }

        public string? StopHotkey { get; set; }

        public double AutoclickerClicksPerSecond { get; set; }

        public string? AutoclickerHotkey { get; set; }

        public int AutoclickerStopAt { get; set; }

        public string? AutoclickerMode { get; set; }

        public string? AutoclickerMouseButton { get; set; }

        public bool AutoclickerBroadcastRelativeSpot { get; set; }
    }
}
