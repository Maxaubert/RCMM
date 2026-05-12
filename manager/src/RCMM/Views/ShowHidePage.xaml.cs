using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace RCMM.Views;

public sealed partial class ShowHidePage : Page
{
    private NavArgs _args = null!;

    public ShowHidePage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = (NavArgs)e.Parameter;
        var vm = _args.ViewModel;
        int builtIn = vm.AllEntries.Count(r => r.IsBuiltIn);
        AppCount.Text = (vm.AllEntries.Count - builtIn).ToString();
        WindowsCount.Text = builtIn.ToString();
    }

    private void App_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(ScopePage), new NavArgs(_args.ViewModel, ListFilter.ApplicationSpecific));
    }

    private void Windows_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(ScopePage), new NavArgs(_args.ViewModel, ListFilter.WindowsSpecific));
    }
}
