using System;
using System.IO;

namespace OsmoActionViewer.Services;

/// <summary>
/// Remembers the last opened folder. Stored next to the executable config so
/// that reinstalling the app preserves it if the user copies settings; the
/// actual user data is in the video folder itself.
/// </summary>
public sealed class FolderPersistence
{
    private readonly string _statePath;

    public FolderPersistence()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OsmoActionViewer");
        Directory.CreateDirectory(dir);
        _statePath = Path.Combine(dir, "last_folder.txt");
    }

    public string? Load()
    {
        try
        {
            if (!File.Exists(_statePath)) return null;
            var value = File.ReadAllText(_statePath).Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    public void Save(string folderPath)
    {
        try
        {
            File.WriteAllText(_statePath, folderPath);
        }
        catch
        {
            // Non-fatal.
        }
    }
}
