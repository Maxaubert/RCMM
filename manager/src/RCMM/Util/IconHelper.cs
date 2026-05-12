using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RCMM.Util;

internal static class IconHelper
{
    /// <summary>
    /// Loads an icon from a shell-style path. Accepts:
    ///   <list type="bullet">
    ///   <item>raw path: <c>C:\Program Files\VLC\vlc.exe</c> — first icon group</item>
    ///   <item>path with positive index: <c>vlc.exe,0</c> — Nth icon group</item>
    ///   <item>path with negative index: <c>imageres.dll,-5302</c> — resource ID 5302</item>
    ///   <item><c>%SystemRoot%</c>-style env vars</item>
    ///   <item>quoted paths and trailing command-line arguments (stripped)</item>
    ///   </list>
    /// Always tries the small icon (16x16) first; falls back to the large
    /// icon if no small one is present. Returns null on any failure.
    /// </summary>
    public static async Task<BitmapImage?> LoadIconAsync(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        try
        {
            var (path, index) = ParseIconSpec(filePath);
            if (!File.Exists(path)) return null;

            // First try ExtractIconEx so we respect the icon index. Falls back
            // to ExtractAssociatedIcon (first icon, 32x32) if that returns nothing.
            using var icon = ExtractWithIndex(path, index) ?? System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;

            using var bitmap = icon.ToBitmap();
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(ms.AsRandomAccessStream());
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static (string path, int index) ParseIconSpec(string raw)
    {
        var expanded = Environment.ExpandEnvironmentVariables(raw).Trim();
        // strip quotes
        if (expanded.StartsWith('"') && expanded.EndsWith('"') && expanded.Length >= 2)
            expanded = expanded[1..^1];

        // detect "path,index" — must be after the last backslash so we don't trip on commas in folder names
        int comma = expanded.LastIndexOf(',');
        int lastSep = expanded.LastIndexOf('\\');
        int index = 0;
        if (comma > lastSep && comma > 0 &&
            int.TryParse(expanded.AsSpan(comma + 1).Trim(), out var parsed))
        {
            index = parsed;
            expanded = expanded[..comma].Trim();
        }

        // some registry entries embed command-line args after the exe — strip them.
        // For unquoted command lines, the first space after the path delimits args.
        // We only do this if no comma index was present and the path doesn't exist
        // as-is (so we don't misinterpret real paths with spaces).
        if (!File.Exists(expanded))
        {
            var firstSpace = expanded.IndexOf(' ');
            if (firstSpace > 0)
            {
                var candidate = expanded[..firstSpace];
                if (File.Exists(candidate)) expanded = candidate;
            }
        }

        return (expanded, index);
    }

    private static System.Drawing.Icon? ExtractWithIndex(string path, int index)
    {
        var small = new IntPtr[1];
        try
        {
            int got = ExtractIconEx(path, index, null, small, 1);
            if (got <= 0 || small[0] == IntPtr.Zero)
            {
                // try the large icon
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
