using System.Runtime.InteropServices;

namespace FaRajni.App.Services;

/// <summary>Explorer's file-name order ("ep 2" before "ep 10").</summary>
internal sealed class NaturalOrder : IComparer<string?>
{
    public static readonly NaturalOrder Instance = new();

    public int Compare(string? a, string? b) => StrCmpLogicalW(a ?? "", b ?? "");

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);
}
