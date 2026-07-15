using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using RCMM.Core.ViewModels;
using Windows.UI;

namespace RCMM.Views;

public sealed partial class ScopePage : Page
{
    private MainViewModel _vm = null!;
    private OriginFilter _origin = OriginFilter.All;
    private VisibilityFilter _visibility = VisibilityFilter.All;

    // Faint lime-tinted hover (~6% alpha) to match the card glow
    private static readonly SolidColorBrush HoverBrush = new(Color.FromArgb(0x10, 0xd4, 0xff, 0x3a));

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

    /// <summary>Adaptive columns: as many ~360px-minimum tiles as fit, tiles
    /// stretch to share the row exactly. One column below 720px.</summary>
    private void EntriesGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (EntriesGrid.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        const double minTile = 360;
        double width = e.NewSize.Width;
        if (width <= 0) return;
        int columns = Math.Max(1, (int)(width / minTile));
        panel.ItemWidth = Math.Floor(width / columns);
    }

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Background = HoverBrush;
    }

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Tiles rest on AppSurface (not transparent like the old full-width rows).
        if (sender is Border b) b.Background = (Brush)Application.Current.Resources["AppSurface"];
    }

    private void Row_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // If the tap came from inside the ToggleSwitch, let it handle itself.
        if (e.OriginalSource is DependencyObject src && FindAncestor<ToggleSwitch>(src) != null) return;
        if (sender is FrameworkElement fe && fe.DataContext is EntryRowViewModel vm && vm.CanHide)
        {
            vm.IsHidden = !vm.IsHidden;
        }
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}
