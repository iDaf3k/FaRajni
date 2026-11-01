using System.Diagnostics;
using Microsoft.Win32;

namespace FaRajni.App.Services;

public static class DefaultPlayerRegistration
{
    private const string ProgId = "FaRajni.Video";
    private static readonly string[] Extensions =
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".webm", ".flv", ".mpg", ".mpeg", ".ts", ".3gp" };

    /// <summary>
    /// Registers a ProgId and file-type associations under HKEY_CURRENT_USER,
    /// then opens Windows Default Apps so the user can confirm the default.
    /// </summary>
    public static void RegisterAndOpenSettings()
    {
        var exePath = Process.GetCurrentProcess().MainModule!.FileName;

        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue("", "FaRajni Video");
            using var cmd = progId.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exePath}\" \"%1\"");
        }

        foreach (var ext in Extensions)
        {
            using var extKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\{ext}\OpenWithProgids");
            extKey.SetValue(ProgId, new byte[0], RegistryValueKind.None);
        }

        // Windows 11 requires the user to confirm the default in Settings.
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
    }
}
