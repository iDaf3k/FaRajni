using System.Text.Json;
using FaRajni.Core.Models;

namespace FaRajni.App.Services;

/// <summary>Small user preferences, saved as JSON next to the library.</summary>
public sealed class AppSettings
{
    public VideoSort Sort { get; set; } = VideoSort.RecentlyAdded;
    /// <summary>Set once dates added come from the files themselves: that first run switches the
    /// sort to "Date added" and rescans so existing rows lose their old scan-time dates.</summary>
    public bool DateAddedFixed { get; set; }
    public bool HideDuplicates { get; set; }
    public int Volume { get; set; } = 100;
    public bool LegacyImportDone { get; set; }
    public double TileSize { get; set; } = 240;

    private System.Windows.Threading.DispatcherTimer? _saveTimer;

    /// <summary>Save shortly after the last change (the size slider fires many changes per drag).</summary>
    public void SaveSoon()
    {
        _saveTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick -= OnSaveTick;
        _saveTimer.Tick += OnSaveTick;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnSaveTick(object? s, EventArgs e) { _saveTimer!.Stop(); Save(); }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsPath)) ?? new();
        }
        catch (Exception ex) { AppPaths.LogError("AppSettings.Load", ex); }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(AppPaths.SettingsPath, JsonSerializer.Serialize(this)); }
        catch (Exception ex) { AppPaths.LogError("AppSettings.Save", ex); }
    }
}
