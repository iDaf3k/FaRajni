using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FaRajni.App.Interop;

/// <summary>Thumbnails and durations from the Windows Shell — the same cached images and
/// metadata Explorer uses, so no video decoding is needed to fill the library.
/// Call from an STA thread.</summary>
internal static class ShellMedia
{
    /// <summary>A thumbnail of the video, or null when Windows can't make one (unknown codec).</summary>
    public static BitmapSource? GetThumbnail(string path, int width)
    {
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var obj) != 0)
            return null;
        var factory = (IShellItemImageFactory)obj;
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var size = new SIZE { cx = width, cy = width * 9 / 16 };
            if (factory.GetImage(size, SIIGBF.ThumbnailOnly | SIIGBF.BiggerSizeOk, out hbmp) != 0 || hbmp == IntPtr.Zero)
                return null;
            var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally
        {
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Duration from the file's media properties (0 when unknown).</summary>
    public static long GetDurationMs(string path)
    {
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem2).GUID, out var obj) != 0)
            return 0;
        var item = (IShellItem2)obj;
        try
        {
            var key = new PROPERTYKEY { fmtid = new Guid("64440490-4C8B-11D1-8B70-080036B11A03"), pid = 3 };   // System.Media.Duration
            return item.GetUInt64(ref key, out var hundredNs) == 0 ? (long)(hundredNs / 10_000) : 0;
        }
        catch { return 0; }
        finally { Marshal.ReleaseComObject(item); }
    }

    // ---------------- COM ----------------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
                                                          [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [Flags]
    private enum SIIGBF { ResizeToFit = 0, BiggerSizeOk = 1, MemoryOnly = 2, IconOnly = 4, ThumbnailOnly = 8, InCacheOnly = 16 }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93")]
    private interface IShellItem2
    {
        // IShellItem
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IntPtr ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IntPtr psi, uint hint, out int piOrder);
        // IShellItem2
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyStoreWithCreateObject(int flags, IntPtr punkCreateObject, ref Guid riid, out IntPtr ppv);
        void GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(ref PROPERTYKEY keyType, ref Guid riid, out IntPtr ppv);
        void Update(IntPtr pbc);
        void GetProperty(ref PROPERTYKEY key, IntPtr ppropvar);
        void GetCLSID(ref PROPERTYKEY key, out Guid pclsid);
        void GetFileTime(ref PROPERTYKEY key, out long pft);
        void GetInt32(ref PROPERTYKEY key, out int pi);
        void GetString(ref PROPERTYKEY key, out IntPtr ppsz);
        void GetUInt32(ref PROPERTYKEY key, out uint pui);
        [PreserveSig] int GetUInt64(ref PROPERTYKEY key, out ulong pull);
        void GetBool(ref PROPERTYKEY key, out int pf);
    }
}
