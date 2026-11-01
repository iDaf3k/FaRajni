using Microsoft.VisualBasic.FileIO;

namespace FaRajni.App.Services;

/// <summary>Recoverable delete: moves a file to the Windows Recycle Bin.</summary>
public static class FileRecycler
{
    /// <returns>null on success (a file that is already gone counts as success), else the error.</returns>
    public static string? MoveToRecycleBin(string path)
    {
        try
        {
            if (File.Exists(path))
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
