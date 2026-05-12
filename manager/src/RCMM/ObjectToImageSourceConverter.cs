using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace RCMM;

/// <summary>
/// Casts an <see langword="object?"/> to <see cref="ImageSource"/>
/// so that <see cref="Microsoft.UI.Xaml.Controls.Image.Source"/>
/// can be bound via x:Bind to a Core ViewModel property typed as object?.
/// </summary>
public sealed class ObjectToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
        => value as ImageSource;

    public object? ConvertBack(object value, Type targetType, object parameter, string language)
        => value;
}
