using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace RCMM.Util;

internal static class IconHelper
{
    /// <summary>
    /// Extracts an icon from a shell-style spec and returns its PNG bytes. Safe to
    /// call from any thread — does only file I/O, GDI+ and Win32 work. Callers
    /// should hand the bytes to the UI thread to construct a BitmapImage (which
    /// is COM-tied to the dispatcher that created it).
    ///
    /// Accepts:
    ///   <list type="bullet">
    ///   <item>raw path: <c>C:\Program Files\VLC\vlc.exe</c></item>
    ///   <item>path with positive index: <c>vlc.exe,0</c></item>
    ///   <item>path with negative resource ID: <c>imageres.dll,-5302</c></item>
    ///   <item><c>%SystemRoot%</c> env vars and bare DLL names (resolved via System32)</item>
    ///   <item>quoted paths with trailing index after the closing quote</item>
    ///   </list>
    /// </summary>
    public static Task<byte[]?> LoadIconBytesAsync(string? filePath)
        => Task.Run<byte[]?>(() =>
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            try
            {
                var (path, index) = ParseIconSpec(filePath);
                if (!File.Exists(path)) return null;

                // ExtractIconEx is precise. ExtractAssociatedIcon as fallback is OK
                // for executables and shortcuts (gives the program's icon) but for
                // DLLs it returns Windows' generic "library" placeholder when the
                // file has no icon resources — better to show no icon than that.
                var icon = ExtractWithIndex(path, index);
                if (icon == null && !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon == null) return null;
                using var iconHandle = icon;
                using var bitmap = iconHandle.ToBitmap();
                using var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        });

    private static (string path, int index) ParseIconSpec(string raw)
    {
        var s = Environment.ExpandEnvironmentVariables(raw).Trim();
        if (s.StartsWith('@')) s = s[1..];

        // path,index detection must come BEFORE quote-stripping because the index
        // sits outside the quotes: "C:\path with space\vlc.exe",0
        int comma = s.LastIndexOf(',');
        int lastSep = s.LastIndexOf('\\');
        int index = 0;
        if (comma > lastSep && comma > 0 &&
            int.TryParse(s.AsSpan(comma + 1).Trim(), out var parsed))
        {
            index = parsed;
            s = s[..comma].Trim();
        }

        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];

        // unquoted command-line: prefer the prefix that names a real file.
        if (!File.Exists(s))
        {
            var firstSpace = s.IndexOf(' ');
            if (firstSpace > 0)
            {
                var candidate = s[..firstSpace];
                if (File.Exists(candidate)) s = candidate;
            }
        }

        // bare filename like "imageres.dll" — fall back to System32 search path.
        if (!File.Exists(s) && !s.Contains('\\') && !s.Contains('/'))
        {
            var sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), s);
            if (File.Exists(sys32)) s = sys32;
        }

        return (s, index);
    }

    private static System.Drawing.Icon? ExtractWithIndex(string path, int index)
    {
        var small = new IntPtr[1];
        try
        {
            int got = ExtractIconEx(path, index, null, small, 1);
            if (got <= 0 || small[0] == IntPtr.Zero)
            {
                var large = new IntPtr[1];
                got = ExtractIconEx(path, index, large, null, 1);
                if (got <= 0 || large[0] == IntPtr.Zero) return null;
                try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(large[0]).Clone(); }
                finally { DestroyIcon(large[0]); }
            }
            try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(small[0]).Clone(); }
            finally { DestroyIcon(small[0]); }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = false)]
    private static extern int ExtractIconEx(string lpszFile, int nIconIndex,
        IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
