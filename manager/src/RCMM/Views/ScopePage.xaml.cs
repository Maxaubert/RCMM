using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using RCMM.Core.ViewModels;

namespace RCMM.Views;

public sealed partial class ScopePage : Page
{
    private MainViewModel _vm = null!;
    private OriginFilter _origin = OriginFilter.All;
    private VisibilityFilter _visibility = VisibilityFilter.All;

    public ScopePage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var args = (NavArgs)e.Parameter;
        _vm = args.ViewModel;
        _vm.RescanComplete += OnRescanComplete;
        ApplyChipStyles();
        RebuildList();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_vm != null) _vm.RescanComplete -= OnRescanComplete;
    }

    private void OnRescanComplete()
    {
        DispatcherQueue.TryEnqueue(RebuildList);
    }

    private void OriginChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        _origin = tag switch
        {
            "apps"    => OriginFilter.Apps,
            "windows" => OriginFilter.Windows,
            _         => OriginFilter.All,
        };
        ApplyChipStyles();
        RebuildList();
    }

    private void VisibilityChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        _visibility = tag switch
        {
            "visible" => VisibilityFilter.Visible,
            "hidden"  => VisibilityFilter.Hidden,
            _         => VisibilityFilter.All,
        };
        ApplyChipStyles();
        RebuildList();
    }

    /// <summary>Active chip gets the lime accent; the rest sit on the surface
    /// color with a subtle border. Same pattern as TemplatesPage.ApplyChipStyles —
    /// geometry is owned by ChipButton in App.xaml, this only flips colors.</summary>
    private void ApplyChipStyles()
    {
        foreach (var (chip, active) in new[]
        {
            (ChipOriginAll, _origin == OriginFilter.All),
            (ChipApps,      _origin == OriginFilter.Apps),
            (ChipWindows,   _origin == OriginFilter.Windows),
            (ChipVisAll,    _visibility == VisibilityFilter.All),
            (ChipVisible,   _visibility == VisibilityFilter.Visible),
            (ChipHidden,    _visibility == VisibilityFilter.Hidden),
        })
        {
            chip.Background = (Brush)Application.Current.Resources[active ? "AppAccent" : "AppSurface"];
            chip.BorderBrush = (Brush)Application.Current.Resources[active ? "AppAccent" : "AppBorder"];
            chip.Foreground = active
                ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 10, 10, 10))
                : (Brush)Application.Current.Resources["AppText"];
        }
    }

    /// <summary>
    /// Re-filter and re-source the grid. Deliberately NOT wired to per-row
    /// IsHidden changes: with the Visible/Hidden chip active, a just-toggled
    /// tile stays put until the next rebuild (chip click, search, rescan)
    /// instead of vanishing under the cursor mid-toggle.
    /// </summary>
    private void RebuildList()
    {
        int apps = 0, windows = 0;
        foreach (var r in _vm.AllEntries) { if (r.IsBuiltIn) windows++; else apps++; }
        ChipApps.Content = $"Apps ({apps})";
        ChipWindows.Content = $"Windows ({windows})";

        var needle = SearchBox.Text;
        EntriesGrid.ItemsSource = _vm.AllEntries
            .Where(r => EntryListFilter.Matches(r, _origin, _visibility, needle))
            .ToList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildList();

    /// <summary>Adaptive square tiles: as many ~132px-minimum squares as fit,
    /// sharing the row width exactly. ItemHeight tracks ItemWidth so the grid
    /// stays square regardless of viewport.</summary>
    private void ApplyItemSizing(double width)
    {
        if (width <= 0) return;
        if (EntriesGrid.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        const double minTile = 132;
        int columns = Math.Max(1, (int)(width / minTile));
        var size = Math.Floor(width / columns);
        panel.ItemWidth = size;
        panel.ItemHeight = size;
    }

    private void EntriesGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyItemSizing(e.NewSize.Width);

    /// <summary>First layout: SizeChanged can fire before ItemsPanelRoot exists,
    /// which left tiles content-sized until the user resized the window. Loaded
    /// runs after the panel materializes, so sizing applies on first render.</summary>
    private void EntriesGrid_Loaded(object sender, RoutedEventArgs e)
        => ApplyItemSizing(EntriesGrid.ActualWidth);

    /// <summary>The whole tile is the toggle. ItemClick fires for mouse, touch,
    /// and Enter/Space on a focused tile, so hide/unhide stays keyboard-operable
    /// without a per-tile switch.</summary>
    private void EntriesGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EntryRowViewModel vm && vm.CanHide)
            vm.IsHidden = !vm.IsHidden;
    }

    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // Hidden tiles brighten to full opacity on hover so their label is
        // readable before you decide to unhide them.
        if (sender is Border b) b.Opacity = 1.0;
    }

    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b && b.DataContext is EntryRowViewModel vm)
            b.Opacity = vm.IsHidden ? HiddenToTileOpacityConverter.HiddenOpacity : 1.0;
    }
}
