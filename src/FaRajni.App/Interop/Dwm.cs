using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FaRajni.App.Interop;

/// <summary>Dark title bar that matches the night theme (Windows 10 20H1+ / Windows 11).</summary>
internal static class Dwm
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_BORDER_COLOR = 34;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void ApplyNight(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        int on = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        int caption = 0x00150C0A;   // COLORREF 0x00BBGGRR → #0A0C15
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref caption, sizeof(int));
    }
}
