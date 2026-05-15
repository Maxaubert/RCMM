using System.Collections.Concurrent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RCMM.Core.Services;

namespace RCMM.Util;

/// <summary>
/// Bridges <see cref="IconLibrary"/>'s string-based path data to a WinUI 3
/// <see cref="Path"/> visual. Each library icon's Geometry is parsed once via
/// XamlReader (the XAML mini-language is the only string→Geometry route in
/// WinUI 3 without a manual parser) and cached.
/// </summary>
public static class IconRender
{
    private static readonly ConcurrentDictionary<string, Geometry?> _geometryCache = new();

    /// <summary>
    /// Returns the Geometry for a library icon name (no <c>lib:</c> prefix)
    /// or null when the name doesn't resolve. The same Geometry instance is
    /// reused across all Path consumers — Geometry isn't a FrameworkElement
    /// so it doesn't carry visual-tree state.
    /// </summary>
    public static Geometry? GetGeometry(string libName)
        => _geometryCache.GetOrAdd(libName, name =>
        {
            var data = IconLibrary.TryGetPathData(name);
            if (string.IsNullOrEmpty(data)) return null;
            // The XAML mini-language for Geometry is the only supported
            // string→Geometry path in WinUI 3. Path Data values come from our
            // own hardcoded library, not user input, so XamlReader is safe.
            var xaml = "<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\""
                       + System.Net.WebUtility.HtmlEncode(data) + "\" />";
            try
            {
                var p = (Path)XamlReader.Load(xaml);
                return p.Data;
            }
            catch
            {
                return null;
            }
        });

    /// <summary>
    /// Builds a Path UIElement for the given icon value (e.g. <c>"lib:terminal"</c>).
    /// Returns null when the value isn't a library reference or the icon doesn't
    /// resolve. The Path's Stroke is wired to the supplied brush; consumers
    /// typically use <see cref="GetGeometry"/> + a XAML template instead.
    /// </summary>
    public static Path? BuildIconElement(string iconValue, double size, Brush stroke, double thickness = 1.75)
    {
        if (!IconLibrary.IsLibraryName(iconValue)) return null;
        var name = IconLibrary.StripPrefix(iconValue);
        if (name == null) return null;
        var geom = GetGeometry(name);
        if (geom == null) return null;
        return new Path
        {
            Data = geom,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = null,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
