using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RCMM.Core.Services;
using RCMM.Util;

namespace RCMM.Views;

/// <summary>
/// Modal icon-picker dialog. Built entirely in code so the row + grid layout
/// can be assembled in one pass without a XAML/code-behind split for what is
/// essentially a one-off control. Returns the user's pick via
/// <see cref="PickedValue"/> after <see cref="ShowAsync"/> resolves.
/// </summary>
public sealed class IconPickerDialog : ContentDialog
{
    private const int Columns = 9;

    public string? PickedValue { get; private set; }

    private readonly StackPanel _body = new() { Spacing = 14 };
    private readonly TextBox _searchBox = new() { PlaceholderText = "Search icons (terminal, folder, copy …)" };

    public IconPickerDialog(string? currentValue)
    {
        PickedValue = currentValue;
        Title = "Choose icon";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Close;
        Background = (Brush)Application.Current.Resources["AppSurface"];
        Foreground = (Brush)Application.Current.Resources["AppText"];

        var outer = new StackPanel { Spacing = 10, MinWidth = 600, MaxWidth = 660 };
        _searchBox.TextChanged += (_, __) => RenderGrid(_searchBox.Text ?? "");
        outer.Children.Add(_searchBox);
        var scroller = new ScrollViewer
        {
            Content = _body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 460,
        };
        outer.Children.Add(scroller);
        Content = outer;

        RenderGrid("");
    }

    private void RenderGrid(string query)
    {
        _body.Children.Clear();
        var q = query.Trim().ToLowerInvariant();
        bool any = false;
        foreach (var cat in IconLibrary.Categories)
        {
            var matches = cat.Icons
                .Where(n => q.Length == 0 || n.ToLowerInvariant().Contains(q) || cat.Name.ToLowerInvariant().Contains(q))
                .ToList();
            if (matches.Count == 0) continue;
            any = true;

            _body.Children.Add(new TextBlock
            {
                Text = cat.Name.ToUpperInvariant(),
                Foreground = (Brush)Application.Current.Resources["AppTextMuted"],
                FontSize = 11,
                CharacterSpacing = 80,
                Margin = new Thickness(2, 8, 0, 2),
            });

            var grid = new Grid();
            for (int c = 0; c < Columns; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int row = 0, col = 0;
            foreach (var name in matches)
            {
                if (col == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var tile = BuildTile(name);
                tile.Margin = new Thickness(3);
                Grid.SetRow(tile, row);
                Grid.SetColumn(tile, col);
                grid.Children.Add(tile);
                col++;
                if (col >= Columns) { col = 0; row++; }
            }
            _body.Children.Add(grid);
        }
        if (!any)
        {
            _body.Children.Add(new TextBlock
            {
                Text = $"No icons match \"{query}\"",
                Foreground = (Brush)Application.Current.Resources["AppTextMuted"],
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 30, 0, 30),
            });
        }
    }

    private Button BuildTile(string name)
    {
        var btn = new Button
        {
            Background = (Brush)Application.Current.Resources["AppSurfaceHover"],
            BorderBrush = (Brush)Application.Current.Resources["AppBorder"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(0),
            Height = 56,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var path = IconRender.BuildIconElement(IconLibrary.MakeLibValue(name), 24,
            (Brush)Application.Current.Resources["AppText"], thickness: 2);
        if (path != null) btn.Content = path;
        if (PickedValue == IconLibrary.MakeLibValue(name))
        {
            btn.BorderBrush = (Brush)Application.Current.Resources["AppAccent"];
            btn.BorderThickness = new Thickness(2);
        }
        ToolTipService.SetToolTip(btn, name);
        btn.Click += (_, __) =>
        {
            PickedValue = IconLibrary.MakeLibValue(name);
            Hide();
        };
        return btn;
    }
}
