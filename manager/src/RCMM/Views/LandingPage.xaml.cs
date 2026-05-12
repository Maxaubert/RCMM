using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RCMM.Core.ViewModels;

namespace RCMM.Views;

public sealed partial class LandingPage : Page
{
    private MainViewModel _vm = null!;

    public LandingPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _vm = (MainViewModel)e.Parameter;
        // Hub bypassed in Plan 1's final wire-up; this page is currently unused.
        // Keeping the class for potential reuse in Plan 3 (modern menu hub).
    }

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        // Unused — LandingPage is bypassed; MainWindow navigates directly to ScopePage.
    }
}
