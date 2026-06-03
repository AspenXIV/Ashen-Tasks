namespace AshenTasker.Models.Windowing;

public sealed record WindowInfo(
    nint Handle,
    string Title,
    string ProcessName,
    int ProcessId,
    int Left,
    int Top,
    int Right,
    int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public string DisplayName => $"{ProcessName} - {Title}";
}
