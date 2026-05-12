using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RCMM.Core.Services;

public sealed class Win32MuiStringResolver : IMuiStringResolver
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SHLoadIndirectString")]
    private static extern int SHLoadIndirectString(
        string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    public string? Resolve(string? mui)
    {
        if (string.IsNullOrEmpty(mui)) return null;
        if (mui[0] != '@') return mui;
        try
        {
            var buf = new StringBuilder(1024);
            var hr = SHLoadIndirectString(mui, buf, buf.Capacity, IntPtr.Zero);
            return hr == 0 ? buf.ToString() : null;
        }
        catch
        {
            return null;
        }
    }
}
