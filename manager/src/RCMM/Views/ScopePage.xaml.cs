using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RCMM.Core.ViewModels;

namespace RCMM.Views;

public sealed partial class ScopePage : Page
{
    private MainViewModel _vm = null!;

    public ScopePage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _vm = (MainViewModel)e.Parameter;
        ScopeTitle.Text = "Right-click menu entries";
        EntriesList.ItemsSource = _vm.AllEntries;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var needle = SearchBox.Text?.Trim() ?? "";
        if (needle.Length == 0)
        {
            EntriesList.ItemsSource = _vm.AllEntries;
            return;
        }
        EntriesList.ItemsSource = _vm.AllEntries
            .Where(r => r.DisplayName.Contains(needle, System.StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
