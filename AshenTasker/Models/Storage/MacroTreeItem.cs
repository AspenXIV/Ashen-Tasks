namespace AshenTasker.Models.Storage;

public sealed class MacroTreeItem
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public required bool IsDirectory { get; init; }

    public List<MacroTreeItem> Children { get; } = [];
}
