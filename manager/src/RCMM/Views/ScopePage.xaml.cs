using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;
using Windows.UI;

namespace RCMM.Views;

public sealed partial class ScopePage : Page
{
    private MainViewModel _vm = null!;
    private ListFilter _filter;
    // Faint lime-tinted hover (~6% alpha) to match the card glow
    private static readonly SolidColorBrush HoverBrush = new(Color.FromArgb(0x10, 0xd4, 0xff, 0x3a));
    private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.Transparent);

    public ScopePage() { InitializeComponent(); }

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Background = HoverBrush;
    }

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Background = TransparentBrush;
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

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var args = (NavArgs)e.Parameter;
        _vm = args.ViewModel;
        _filter = args.Filter;

        ConfigureHeading();
        _vm.RescanComplete += OnRescanComplete;
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

    private void ConfigureHeading()
    {
        HeadingTitle.Text = _filter switch
        {
            ListFilter.ApplicationSpecific => "Application specific",
            ListFilter.WindowsSpecific => "Windows specific",
            _ => "All entries",
        };
    }

    private void RebuildList()
    {
        var filtered = _vm.AllEntries.Where(MatchesFilter);
        var needle = SearchBox.Text?.Trim() ?? "";
        if (needle.Length > 0)
            filtered = filtered.Where(r => r.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase));
        var list = filtered.ToList();
        EntriesList.ItemsSource = list;

    }

    private bool MatchesFilter(EntryRowViewModel r) => _filter switch
    {
        ListFilter.ApplicationSpecific => !r.IsBuiltIn,
        ListFilter.WindowsSpecific => r.IsBuiltIn,
        _ => true,
    };

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildList();

}
