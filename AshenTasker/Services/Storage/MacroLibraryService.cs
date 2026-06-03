using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AshenTasker.Configuration;
using AshenTasker.Models.Macros;
using AshenTasker.Models.Storage;

namespace AshenTasker.Services.Storage;

public sealed class MacroLibraryService
{
    private const string MacroExtension = ".macro.json";
    private static readonly JsonSerializerOptions MacroJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public MacroLibraryService()
    {
        RootDirectory = ResolveRootDirectory();
        Directory.CreateDirectory(RootDirectory);
    }

    public string RootDirectory { get; }

    public MacroTreeItem LoadTree()
    {
        DirectoryInfo root = new(RootDirectory);
        return LoadDirectory(root);
    }

    public string CreateMacro(string? directoryPath = null, string baseName = "New Macro")
    {
        string targetDirectory = NormalizeDirectory(directoryPath);
        Directory.CreateDirectory(targetDirectory);

        string macroPath = GetAvailablePath(targetDirectory, baseName, MacroExtension);
        WriteMacroFile(macroPath, "New Macro");
        return macroPath;
    }

    public string CreateFolder(string? parentDirectoryPath = null, string baseName = "New Folder")
    {
        string targetDirectory = NormalizeDirectory(parentDirectoryPath);
        string folderPath = GetAvailablePath(targetDirectory, baseName, string.Empty);
        Directory.CreateDirectory(folderPath);
        return folderPath;
    }

    public string Rename(string path, string newName)
    {
        EnsureInsideRoot(path);

        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new InvalidOperationException("Name cannot be blank.");
        }

        string sanitizedName = SanitizeName(newName);
        bool isDirectory = Directory.Exists(path);
        string parent = Path.GetDirectoryName(path) ?? RootDirectory;
        string extension = isDirectory ? string.Empty : Path.GetExtension(path);
        string destination = Path.Combine(parent, isDirectory ? sanitizedName : EnsureMacroFileName(sanitizedName, extension));

        EnsureInsideRoot(destination);

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new InvalidOperationException("A file or folder with that name already exists.");
        }

        if (isDirectory)
        {
            Directory.Move(path, destination);
        }
        else
        {
            File.Move(path, destination);
        }

        return destination;
    }

    public void Delete(string path)
    {
        EnsureInsideRoot(path);

        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(RootDirectory), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The root Macros folder cannot be deleted.");
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            return;
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void SaveMacro(string path)
    {
        EnsureInsideRoot(path);
        WriteMacroFile(path, Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path)));
    }

    public string ReadMacroText(string path)
    {
        EnsureInsideRoot(path);
        return File.ReadAllText(path);
    }

    public void SaveMacroText(string path, string text)
    {
        EnsureInsideRoot(path);
        File.WriteAllText(path, text);
    }

    public MacroDocument ReadMacroDocument(string path)
    {
        EnsureInsideRoot(path);

        try
        {
            MacroDocument? document = JsonSerializer.Deserialize<MacroDocument>(File.ReadAllText(path), MacroJsonOptions);
            return document ?? new MacroDocument { Name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path)) };
        }
        catch
        {
            return new MacroDocument { Name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path)) };
        }
    }

    public void SaveMacroDocument(string path, MacroDocument document)
    {
        EnsureInsideRoot(path);
        document.UpdatedUtc = DateTime.UtcNow;
        File.WriteAllText(path, JsonSerializer.Serialize(document, MacroJsonOptions));
    }

    public bool IsMacroFile(string path)
    {
        return File.Exists(path) && path.EndsWith(MacroExtension, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsDirectory(string path)
    {
        return Directory.Exists(path);
    }

    private MacroTreeItem LoadDirectory(DirectoryInfo directory)
    {
        MacroTreeItem item = new()
        {
            Name = directory.Name,
            FullPath = directory.FullName,
            IsDirectory = true
        };

        foreach (DirectoryInfo childDirectory in directory.EnumerateDirectories().OrderBy(child => child.Name))
        {
            item.Children.Add(LoadDirectory(childDirectory));
        }

        foreach (FileInfo macroFile in directory.EnumerateFiles($"*{MacroExtension}").OrderBy(file => file.Name))
        {
            item.Children.Add(new MacroTreeItem
            {
                Name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(macroFile.Name)),
                FullPath = macroFile.FullName,
                IsDirectory = false
            });
        }

        return item;
    }

    private void WriteMacroFile(string path, string name)
    {
        EnsureInsideRoot(path);

        MacroDocument macro = new()
        {
            Name = name,
            Version = 1,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        string json = JsonSerializer.Serialize(macro, MacroJsonOptions);
        File.WriteAllText(path, json);
    }

    private string NormalizeDirectory(string? directoryPath)
    {
        string path = string.IsNullOrWhiteSpace(directoryPath) ? RootDirectory : directoryPath;
        EnsureInsideRoot(path);
        return path;
    }

    private void EnsureInsideRoot(string path)
    {
        string root = Path.GetFullPath(RootDirectory);
        string fullPath = Path.GetFullPath(path);

        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Path is outside the Macros folder.");
        }
    }

    private static string ResolveRootDirectory()
    {
        if (!string.IsNullOrWhiteSpace(AppSettings.MacroDirectory))
        {
            return AppSettings.MacroDirectory!;
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AshenTasker.csproj")))
            {
                return Path.Combine(directory.FullName, "Macros");
            }

            directory = directory.Parent;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AshenTasker",
            "Macros");
    }

    private static string GetAvailablePath(string directory, string baseName, string extension)
    {
        string sanitizedBaseName = SanitizeName(baseName);
        string path = Path.Combine(directory, sanitizedBaseName + extension);
        int index = 2;

        while (File.Exists(path) || Directory.Exists(path))
        {
            path = Path.Combine(directory, $"{sanitizedBaseName} {index}{extension}");
            index++;
        }

        return path;
    }

    private static string SanitizeName(string name)
    {
        string sanitized = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Untitled" : sanitized;
    }

    private static string EnsureMacroFileName(string name, string currentExtension)
    {
        if (name.EndsWith(MacroExtension, StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        if (string.Equals(currentExtension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            return name.EndsWith(".macro", StringComparison.OrdinalIgnoreCase)
                ? $"{name}.json"
                : $"{name}{MacroExtension}";
        }

        return $"{name}{MacroExtension}";
    }
}
