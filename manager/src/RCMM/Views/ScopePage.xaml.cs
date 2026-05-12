using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RCMM.Core.Models;
using RCMM.Core.ViewModels;

namespace RCMM.Views;

public sealed partial class ScopePage : Page
{
    private MainViewModel _vm = null!;
    private Scope _scope;

    public ScopePage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var (vm, scope) = ((MainViewModel, Scope))e.Parameter;
        _vm = vm;
        _scope = scope;
        ScopeTitle.Text = scope switch
        {
            Scope.Files       => "Files",
            Scope.Folders     => "Folders",
            Scope.Drives      => "Drives",
            Scope.Background  => "Desktop & folder background",
            _ => scope.ToString()
        };
        EntriesList.ItemsSource = vm.GetScope(scope).Entries;
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
            EntriesList.ItemsSource = _vm.GetScope(_scope).Entries;
            return;
        }
        EntriesList.ItemsSource = _vm.GetScope(_scope).Entries
            .Where(r => r.DisplayName.Contains(needle, System.StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
