using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace RCMM;

public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, System.Type t, object p, string l) => !(bool)value;
    public object ConvertBack(object value, System.Type t, object p, string l) => !(bool)value;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, System.Type t, object p, string l)
        => (bool)value ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, System.Type t, object p, string l)
        => (Visibility)value == Visibility.Visible;
}

public sealed class InvertBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, System.Type t, object p, string l)
        => (bool)value ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, System.Type t, object p, string l)
        => (Visibility)value == Visibility.Collapsed;
}

// ---- Show/Hide square tiles: the whole tile is the toggle, so the tile's
//      look IS the state. These map IsHidden onto the three visual channels
//      (fill, edge, opacity) so the state survives without a ToggleSwitch.

public sealed class HiddenToTileOpacityConverter : IValueConverter
{
    /// <summary>Shared with ScopePage's hover handlers, which restore this
    /// value on pointer-exit after the hover temporarily brightens a tile.</summary>
    public const double HiddenOpacity = 0.5;

    public object Convert(object value, System.Type t, object p, string l)
        => (bool)value ? HiddenOpacity : 1.0;
    public object ConvertBack(object value, System.Type t, object p, string l)
        => throw new System.NotSupportedException();
}

public sealed class HiddenToTileBorderBrushConverter : IValueConverter
{
    public object Convert(object value, System.Type t, object p, string l)
        => Application.Current.Resources[(bool)value ? "AppBorder" : "AppAccent"];
    public object ConvertBack(object value, System.Type t, object p, string l)
        => throw new System.NotSupportedException();
}
