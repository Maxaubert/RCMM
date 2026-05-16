using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RCMM.Core.Services;

namespace RCMM.Util;

/// <summary>
/// Bridges <see cref="IconLibrary"/>'s string-based path data to a WinUI 3
/// <see cref="Path"/> visual. Each call inflates a fresh Path via XamlReader
/// — we deliberately do not cache Geometry instances because a single
/// <c>Microsoft.UI.Xaml.Media.Geometry</c> may only be assigned to one Path's
/// <c>Data</c>. Reusing a cached Geometry across two Paths throws
/// <c>ArgumentException: Value does not fall within the expected range.</c>
/// (a marshaled E_INVALIDARG) on the second consumer.
///
/// The XAML mini-language is the only string→Geometry route in WinUI 3, and
/// the data strings come from our own hardcoded library — XamlReader is safe.
/// </summary>
public static class IconRender
{
    /// <summary>
    /// Build a fully-configured <see cref="Path"/> for the given library icon
    /// value (e.g. <c>"lib:terminal"</c>). Returns null when the value isn't a
    /// library reference or the icon name doesn't resolve.
    /// </summary>
    public static Path? BuildIconElement(string iconValue, double size, Brush stroke, double thickness = 1.75)
    {
        if (!IconLibrary.IsLibraryName(iconValue)) return null;
        var name = IconLibrary.StripPrefix(iconValue);
        if (name == null) return null;
        var data = IconLibrary.TryGetPathData(name);
        if (string.IsNullOrEmpty(data)) return null;

        // Build the Path entirely via XamlReader so the Geometry is born
        // already parented to this Path — no detach/reparent issues.
        var encoded = System.Net.WebUtility.HtmlEncode(data);
        var xaml = "<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
                 + "Stretch=\"Uniform\" StrokeLineJoin=\"Round\" "
                 + "StrokeStartLineCap=\"Round\" StrokeEndLineCap=\"Round\" "
                 + "Data=\"" + encoded + "\" />";
        try
        {
            var p = (Path)XamlReader.Load(xaml);
            p.Stroke = stroke;
            p.StrokeThickness = thickness;
            p.Width = size;
            p.Height = size;
            p.HorizontalAlignment = HorizontalAlignment.Center;
            p.VerticalAlignment = VerticalAlignment.Center;
            return p;
        }
        catch
        {
            return null;
        }
    }
}
