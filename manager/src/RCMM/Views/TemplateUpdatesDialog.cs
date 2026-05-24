using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RCMM.Core.Diagnostics;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;
using Windows.UI;

namespace RCMM.Views;

/// <summary>
/// "Template updates available" flow. Detects entries whose source template has
/// changed since the user added them (see <see cref="TemplateUpdateService"/>),
/// shows a per-entry checklist, and either merges the chosen updates (keeping the
/// user's name / icon / folder / terminal) or records a Skip so they don't nag
/// again until the template changes further.
///
/// Static + XamlRoot-parameterised so both startup (MainWindow) and the manual
/// "Check for updates" button (AddPage) can drive the same flow.
/// </summary>
public static class TemplateUpdatesDialog
{
    /// <summary>Find updates, prompt, and apply/skip per the user's choice.
    /// <paramref name="manual"/> = true shows an "up to date" confirmation when
    /// nothing is pending (startup stays silent in that case).</summary>
    public static async Task RunAsync(MainViewModel vm, XamlRoot root, bool manual)
    {
        if (vm.AddPage == null || root == null) return;

        var updates = new TemplateUpdateService().FindUpdates(vm.AddPage.Snapshot());
        if (updates.Count == 0)
        {
            Log.Info("tplupd", "no template updates pending");
            if (manual) await InfoAsync(root, "Templates up to date", "None of your added entries have newer template versions.");
            return;
        }
        Log.Info("tplupd", $"{updates.Count} template update(s) available");

        // Custom lime toggle rows instead of WinUI CheckBox: recoloring a CheckBox's
        // checked state needs a theme-resource override, which crashes inside the
        // dialog popup (stowed XAML exception). Assigning app brushes directly to our
        // own elements — the IconPickerDialog pattern — is safe.
        var selected = new HashSet<string>(updates.Select(u => u.Entry.Id));   // all on by default
        var accent = (Brush)Application.Current.Resources["AppAccent"];
        var borderBrush = (Brush)Application.Current.Resources["AppBorder"];
        var onAccent = new SolidColorBrush(Color.FromArgb(255, 0x0A, 0x0A, 0x0A));
        var transparent = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = "These added entries came from built-in templates we've since updated. "
                 + "Updating refreshes the command and file types; your name, icon, folder and terminal are kept.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 8),
        });
        foreach (var u in updates)
        {
            var id = u.Entry.Id;
            var glyph = new TextBlock
            {
                Text = "✓", Foreground = onAccent, FontSize = 13, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var box = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(5),
                Background = accent, BorderBrush = accent, BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0),
                Child = glyph,
            };
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = u.Entry.Name, FontWeight = FontWeights.SemiBold });
            label.Children.Add(new TextBlock { Text = u.Summary, Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap });

            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6), Background = transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(box, 0);
            Grid.SetColumn(label, 1);
            row.Children.Add(box);
            row.Children.Add(label);
            row.Tapped += (_, __) =>
            {
                if (selected.Remove(id)) { box.Background = transparent; box.BorderBrush = borderBrush; glyph.Visibility = Visibility.Collapsed; }
                else { selected.Add(id); box.Background = accent; box.BorderBrush = accent; glyph.Visibility = Visibility.Visible; }
            };
            panel.Children.Add(row);
        }

        var dialog = new ContentDialog
        {
            Title = "Template updates available",
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 380,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            PrimaryButtonText = "Update selected",
            SecondaryButtonText = "Skip all",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
            RequestedTheme = ElementTheme.Dark,
        };
        ApplyTheme(dialog);

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            var chosen = updates.Where(u => selected.Contains(u.Entry.Id)).ToList();
            if (chosen.Count == 0) return;
            foreach (var u in chosen)
            {
                var (command, _) = TemplatesPage.ExpandTemplate(u.Template);   // re-resolve %selfdir% / %bin%
                vm.AddPage.ReplaceEntry(TemplateUpdateService.Merge(u.Entry, u.Template, command));
            }
            Log.Info("tplupd", $"updating {chosen.Count} entr(y/ies)");
            // Reuse the normal apply path: ReplaceEntry marked the page dirty, so
            // ApplyPending re-writes the registry; then refresh Explorer + rescan.
            await Task.Run(() => vm.ApplyPending());
            await Task.Run(() => new ExplorerRestart().Restart());
            await vm.RescanAsync();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            foreach (var u in updates)
                vm.AddPage.ReplaceEntry(TemplateUpdateService.MarkSkipped(u.Entry, u.Template));
            Log.Info("tplupd", $"skipped {updates.Count} update(s)");
            // Skip only changes tracking metadata — persist, no registry write needed.
            new AdditionStore(AdditionStore.DefaultPath()).Save(vm.AddPage.Snapshot());
            vm.AddPage.MarkClean();
        }
        // "Later" (CloseButton): leave everything pending; prompt again next launch.
    }

    private static async Task InfoAsync(XamlRoot root, string title, string body)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            XamlRoot = root,
            RequestedTheme = ElementTheme.Dark,
        };
        await dialog.ShowAsync();
    }

    /// <summary>Nudge a stock ContentDialog toward RCMM's flat-dark + lime look.
    /// We do this the SAFE way only: drop the SystemAccent default button, and apply
    /// button Styles that are BasedOn WinUI's built-in DefaultButtonStyle with just
    /// literal Background/Foreground setters. Two things crash the app here (stowed
    /// XAML exception 0xc000027b) and are deliberately avoided: a custom ControlTemplate
    /// that references {ThemeResource App…} (doesn't resolve in the dialog's popup root),
    /// and overriding theme-resource brush keys on the dialog instance.</summary>
    private static void ApplyTheme(ContentDialog dialog)
    {
        dialog.DefaultButton = ContentDialogButton.None;   // no Windows-blue accent button

        // Darken the dialog's own chrome to the app surface. These are the dialog's
        // OWN keys (resolved when the panel loads) — unlike the nested checkbox/accent
        // keys that crashed earlier, which is why those are now handled differently.
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
        if (baseStyle != null) s.BasedOn = baseStyle;   // inherit the working default template (ThemeResources resolve normally)
        s.Setters.Add(new Setter(Control.BackgroundProperty, background));
        s.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        s.Setters.Add(new Setter(Control.FontWeightProperty, weight));
        return s;
    }

    private static SolidColorBrush B(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));
}
