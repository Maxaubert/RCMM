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
