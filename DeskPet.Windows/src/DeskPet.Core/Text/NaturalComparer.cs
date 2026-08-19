using System.Runtime.InteropServices;

namespace DeskPet.Core.Text;

public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);
    public int Compare(string? a, string? b)
    {
        if (a is null) return b is null ? 0 : -1;
        if (b is null) return 1;
        try { return StrCmpLogicalW(a, b); }
        catch (Exception) { return StringComparer.Ordinal.Compare(a, b); }
    }
}
