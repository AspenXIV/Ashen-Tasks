namespace AshenTasker.Models.Windowing;

public readonly record struct WindowBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}
