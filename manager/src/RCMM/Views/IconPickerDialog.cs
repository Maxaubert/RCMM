using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RCMM.Core.Diagnostics;
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
        Log.Info("iconpicker", "ctor: setting basic props");
        Title = "Choose icon";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Close;

        Log.Info("iconpicker", "ctor: building outer");
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

        Log.Info("iconpicker", "ctor: rendering grid");
        try { RenderGrid(""); Log.Info("iconpicker", "ctor: grid rendered"); }
        catch (Exception ex) { Log.Error("iconpicker", "ctor: RenderGrid threw", ex); throw; }
    }

    private void RenderGrid(string query)
    {
        _body.Children.Clear();
        var q = query.Trim().ToLowerInvariant();
        bool any = false;
        int catIdx = -1;
        foreach (var cat in IconLibrary.Categories)
        {
            catIdx++;
            Log.Debug("iconpicker", $"cat[{catIdx}] {cat.Name}: filtering");
            var matches = cat.Icons
                .Where(n => q.Length == 0 || n.ToLowerInvariant().Contains(q) || cat.Name.ToLowerInvariant().Contains(q))
                .ToList();
            if (matches.Count == 0) continue;
            any = true;

            try
            {
                var head = new TextBlock();
                head.Text = cat.Name;
                head.Foreground = (Brush)Application.Current.Resources["AppTextMuted"];
                head.FontSize = 11;
                head.Margin = new Thickness(2, 8, 0, 2);
                _body.Children.Add(head);
            }
            catch (Exception ex) { Log.Error("iconpicker", $"cat[{catIdx}] head failed", ex); throw; }

            Grid grid;
            try
            {
                grid = new Grid();
                for (int c = 0; c < Columns; c++)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            catch (Exception ex) { Log.Error("iconpicker", $"cat[{catIdx}] grid setup failed", ex); throw; }

            int row = 0, col = 0;
            int tileIdx = -1;
            foreach (var name in matches)
            {
                tileIdx++;
                try
                {
                    if (col == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var tile = BuildTile(name);
                    tile.Margin = new Thickness(3);
                    Grid.SetRow(tile, row);
                    Grid.SetColumn(tile, col);
                    grid.Children.Add(tile);
                }
                catch (Exception ex)
                {
                    Log.Error("iconpicker", $"cat[{catIdx}].tile[{tileIdx}] name={name} failed", ex);
                    throw;
                }
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
        Button btn;
        try
        {
            btn = new Button();
            btn.Background = (Brush)Application.Current.Resources["AppSurfaceHover"];
            btn.BorderBrush = (Brush)Application.Current.Resources["AppBorder"];
            btn.BorderThickness = new Thickness(1);
            btn.CornerRadius = new CornerRadius(7);
            btn.Padding = new Thickness(0);
            btn.Height = 56;
            btn.HorizontalAlignment = HorizontalAlignment.Stretch;
            btn.VerticalAlignment = VerticalAlignment.Stretch;
        }
        catch (Exception ex) { Log.Error("iconpicker", $"BuildTile {name}: btn props failed", ex); throw; }

        try
        {
            var path = IconRender.BuildIconElement(IconLibrary.MakeLibValue(name), 24,
                (Brush)Application.Current.Resources["AppText"], thickness: 2);
            if (path != null) btn.Content = path;
        }
        catch (Exception ex) { Log.Error("iconpicker", $"BuildTile {name}: path failed", ex); throw; }

        try
        {
            if (PickedValue == IconLibrary.MakeLibValue(name))
            {
                btn.BorderBrush = (Brush)Application.Current.Resources["AppAccent"];
                btn.BorderThickness = new Thickness(2);
            }
        }
        catch (Exception ex) { Log.Error("iconpicker", $"BuildTile {name}: highlight failed", ex); throw; }

        try { ToolTipService.SetToolTip(btn, name); }
        catch (Exception ex) { Log.Error("iconpicker", $"BuildTile {name}: tooltip failed", ex); throw; }

        btn.Click += (_, __) =>
        {
            PickedValue = IconLibrary.MakeLibValue(name);
            Hide();
        };
        return btn;
    }
}
