using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using RCMM.Core.Diagnostics;

namespace RCMM.Util;

/// <summary>
/// The single bytes→BitmapImage icon-painting pipeline. Three code-behinds
/// (AddPage, TemplatesPage, MainWindow) used to carry copy-pasted versions of
/// this that had already drifted — one drift shipped a bug where binary-icon
/// entries showed blank on the Add page. Decode failures are logged (one copy
/// used to swallow them silently) and the stream plumbing is the simple
/// MemoryStream form (two copies hand-rolled InMemoryRandomAccessStream +
/// DataWriter for the same result).
/// </summary>
internal static class IconPainter
{
    /// <summary>Decodes PNG/ICO bytes into a BitmapImage. Must run on the UI
    /// thread — BitmapImage is COM-tied to the dispatcher that creates it.
    /// Returns null on decode failure (logged, never thrown).</summary>
    internal static async Task<BitmapImage?> DecodeAsync(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(ms.AsRandomAccessStream());
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Debug("ui", $"icon decode failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extracts an icon from a file/exe/dll/.ico path off the UI thread and
    /// paints it into the named Border host inside <paramref name="container"/>.
    /// Guards on the container's DataContext at each await so a list row
    /// recycled to a different item mid-load isn't painted with a stale icon —
    /// the recycle hazard both page copies handled (slightly differently).
    /// </summary>
    internal static async Task PaintIntoHostAsync(
        FrameworkElement container, object dataContext, string hostName, string path, double size)
    {
        var bytes = await IconHelper.LoadIconBytesAsync(path);
        if (bytes == null || bytes.Length == 0) return;
        if (!ReferenceEquals(container.DataContext, dataContext)) return;
        if (container.FindName(hostName) is not Border host) return;
        var bmp = await DecodeAsync(bytes);
        if (bmp == null) return;
        if (!ReferenceEquals(container.DataContext, dataContext)) return;
        host.Child = new Image { Source = bmp, Width = size, Height = size };
        host.Visibility = Visibility.Visible;
    }
}
