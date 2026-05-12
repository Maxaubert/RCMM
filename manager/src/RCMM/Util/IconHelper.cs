using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RCMM.Util;

internal static class IconHelper
{
    public static async Task<BitmapImage?> LoadIconAsync(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(filePath);

            // Strip trailing ",index" (icon resource indices in shell registry).
            var commaIdx = expanded.LastIndexOf(',');
            if (commaIdx > 0 && commaIdx > expanded.LastIndexOf('\\'))
                expanded = expanded[..commaIdx];

            // Strip wrapping quotes.
            if (expanded.StartsWith('"') && expanded.EndsWith('"') && expanded.Length >= 2)
                expanded = expanded[1..^1];

            if (!File.Exists(expanded)) return null;

            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(expanded);
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
}
