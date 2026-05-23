using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using RCMM.Core.Services;
using RCMM.Util;

namespace RCMM.Views;

/// <summary>
/// Modal icon-picker dialog over the full bundled Lucide set (~1,700 icons).
///
/// The grid is a <see cref="GridView"/> — its built-in virtualization is what
/// makes a set this large usable: only the tiles actually on screen are
/// realized, and each tile's vector <see cref="Microsoft.UI.Xaml.Shapes.Path"/>
/// is built lazily in <see cref="OnContainerContentChanging"/> (and torn down on
/// recycle). A non-virtualized layout would inflate ~1,700 geometries up front
/// and freeze the dialog. Search filters across each icon's name + Lucide
/// tags/aliases (see <see cref="IconLibrary.SearchKeywords"/>).
/// </summary>
public sealed class IconPickerDialog : ContentDialog
{
    public string? PickedValue { get; private set; }

    private readonly TextBox _searchBox = new() { PlaceholderText = "Search icons (terminal, folder, bin, arrow …)" };
    private readonly GridView _grid;
    private readonly TextBlock _emptyText;
    private readonly Brush _stroke;

    /// <summary>One picker section: a category name + its icon names. Inheriting
    /// ObservableCollection gives CollectionViewSource an iterable group.</summary>
    private sealed class IconGroup : ObservableCollection<string>
    {
        public string Key { get; }
        public IconGroup(string key) { Key = key; }
    }

    public IconPickerDialog(string? currentValue)
    {
        PickedValue = currentValue;
        Title = "Choose icon";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Close;
        _stroke = (Brush)Application.Current.Resources["AppText"];

        _grid = new GridView
        {
            IsItemClickEnabled = true,
            SelectionMode = ListViewSelectionMode.None,
            MaxHeight = 460,
            ItemTemplate = (DataTemplate)XamlReader.Load(TileTemplateXaml),
            ItemContainerStyle = BuildContainerStyle(),
        };
        _grid.ItemClick += OnItemClick;
        _grid.ContainerContentChanging += OnContainerContentChanging;
        _grid.GroupStyle.Add(new GroupStyle
        {
            HeaderTemplate = (DataTemplate)XamlReader.Load(HeaderTemplateXaml),
        });

        _emptyText = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["AppTextMuted"],
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 30, 0, 30),
            Visibility = Visibility.Collapsed,
        };

        var outer = new StackPanel { Spacing = 10, MinWidth = 600, MaxWidth = 660 };
        outer.Children.Add(_searchBox);
        outer.Children.Add(_grid);
        outer.Children.Add(_emptyText);
        Content = outer;

        _searchBox.TextChanged += (_, __) => Populate(_searchBox.Text ?? "");
        Populate("");
    }

    private void Populate(string query)
    {
        var q = query.Trim().ToLowerInvariant();
        var groups = new List<IconGroup>();
        foreach (var cat in IconLibrary.Categories)
        {
            IconGroup? group = null;
            foreach (var name in cat.Icons)
            {
                if (q.Length != 0 && !IconLibrary.SearchKeywords(name).Contains(q)) continue;
                group ??= new IconGroup(cat.Name);
                group.Add(name);
            }
            if (group != null) groups.Add(group);
        }

        // Fresh CollectionViewSource each time: re-pointing ItemsSource is the
        // simplest way to force the grouped GridView to re-virtualize the filter.
        var cvs = new CollectionViewSource { IsSourceGrouped = true, Source = groups };
        _grid.ItemsSource = cvs.View;

        bool any = groups.Count > 0;
        _grid.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _emptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (!any) _emptyText.Text = $"No icons match \"{query}\"";
    }

    // Build each visible tile's icon lazily; clear it on recycle. This is the
    // hook that keeps a ~1,700-icon grid responsive.
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer?.ContentTemplateRoot is not Border border) return;
        if (args.InRecycleQueue)
        {
            border.Child = null;
            return;
        }
        if (args.Item is not string name) return;

        var libValue = IconLibrary.MakeLibValue(name);
        border.Child = IconRender.BuildIconElement(libValue, 24, _stroke, thickness: 2);

        bool selected = PickedValue == libValue;
        border.BorderBrush = (Brush)Application.Current.Resources[selected ? "AppAccent" : "AppBorder"];
        border.BorderThickness = new Thickness(selected ? 2 : 1);
        ToolTipService.SetToolTip(border, name);
        args.Handled = true;
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is string name)
        {
            PickedValue = IconLibrary.MakeLibValue(name);
            Hide();
        }
    }

    private static Style BuildContainerStyle()
    {
        // Strip the default GridViewItem chrome so our 56×56 tile is the visual.
        var style = new Style(typeof(GridViewItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(3)));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
        return style;
    }

    private const string TileTemplateXaml =
        "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
        "<Border Width=\"56\" Height=\"56\" CornerRadius=\"7\" " +
        "Background=\"{ThemeResource AppSurfaceHover}\" " +
        "BorderBrush=\"{ThemeResource AppBorder}\" BorderThickness=\"1\"/>" +
        "</DataTemplate>";

    private const string HeaderTemplateXaml =
        "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
        "<TextBlock Text=\"{Binding Key}\" Foreground=\"{ThemeResource AppTextMuted}\" " +
        "FontSize=\"11\" Margin=\"2,8,0,2\"/>" +
        "</DataTemplate>";
}
