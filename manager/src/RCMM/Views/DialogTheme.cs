using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RCMM.Views;

/// <summary>
/// Nudges a stock <see cref="ContentDialog"/> toward RCMM's flat-dark + lime look —
/// the SAFE way only. Two things crash the app here (stowed XAML exception
/// 0xc000027b) and are deliberately avoided: a custom ControlTemplate that
/// references {ThemeResource App…} (won't resolve in the dialog's popup root), and
/// overriding theme-resource brush keys on the dialog instance. So we only drop the
/// Windows-blue default button and apply button Styles BasedOn WinUI's built-in
/// DefaultButtonStyle with literal Background/Foreground setters. (Mirrors the proven
/// approach in <see cref="TemplateUpdatesDialog"/>.)
/// </summary>
internal static class DialogTheme
{
    public static void Apply(ContentDialog dialog)
    {
        dialog.RequestedTheme = ElementTheme.Dark;
        dialog.DefaultButton = ContentDialogButton.None;   // no Windows-blue accent button

        // The dialog's OWN keys (resolved when the panel loads) are safe to set.
        dialog.Resources["ContentDialogBackground"] = B(0x12, 0x12, 0x16);
        dialog.Resources["ContentDialogBorderBrush"] = B(0x2A, 0x2A, 0x31);

        var baseStyle = Application.Current.Resources.TryGetValue("DefaultButtonStyle", out var ds)
            ? ds as Style : null;
        dialog.PrimaryButtonStyle = ButtonStyle(baseStyle, B(0xD4, 0xFF, 0x3A), B(0x0A, 0x0A, 0x0A), FontWeights.SemiBold); // lime
        dialog.SecondaryButtonStyle = ButtonStyle(baseStyle, B(0x1F, 0x1F, 0x25), B(0xF1, 0xF1, 0xF3), FontWeights.Normal); // surface
        dialog.CloseButtonStyle = ButtonStyle(baseStyle, B(0x1F, 0x1F, 0x25), B(0xF1, 0xF1, 0xF3), FontWeights.Normal);     // surface
    }

    private static Style ButtonStyle(Style? baseStyle, Brush background, Brush foreground, Windows.UI.Text.FontWeight weight)
    {
        var s = new Style(typeof(Button));
        if (baseStyle != null) s.BasedOn = baseStyle;
        s.Setters.Add(new Setter(Control.BackgroundProperty, background));
        s.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        s.Setters.Add(new Setter(Control.FontWeightProperty, weight));
        return s;
    }

    private static SolidColorBrush B(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));
}
