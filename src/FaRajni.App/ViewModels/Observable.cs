using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FaRajni.App.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public static class Fmt
{
    public static string Duration(long ms)
    {
        if (ms <= 0) return "–:––";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>"m:ss.s" — the editor's precise time read-out.</summary>
    public static string Precise(long ms)
    {
        var s = Math.Max(0, ms) / 1000.0;
        var m = (int)(s / 60);
        return $"{m}:{s - m * 60:00.0}";
    }

    public static string Size(long bytes)
    {
        if (bytes <= 0) return "";
        var mb = bytes / 1_000_000.0;
        return mb >= 1000 ? $"{mb / 1000:0.0} GB" : $"{Math.Max(1, Math.Round(mb)):0} MB";
    }

    public static string Total(long ms)
    {
        var m = (long)Math.Round(ms / 60000.0);
        return m >= 60 ? $"{m / 60} h {m % 60} m" : $"{m} m";
    }

    public static string Date(DateTime utc) =>
        utc == DateTime.MinValue ? "" : utc.ToLocalTime().ToString("MMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture);

    public static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}
