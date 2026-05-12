using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RCMM.Core.Models;
using RCMM.Core.ViewModels;

namespace RCMM.Views;

public sealed partial class LandingPage : Page
{
    public sealed record CardItem(Scope Scope, string Title, string Subtitle);

    private MainViewModel _vm = null!;

    public LandingPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _vm = (MainViewModel)e.Parameter;
        var cards = new List<CardItem>();
        foreach (var scope in new[] { Scope.Files, Scope.Folders, Scope.Drives, Scope.Background })
        {
            var list = _vm.GetScope(scope).Entries;
            var hidden = list.Count(r => r.IsHidden);
            cards.Add(new CardItem(scope, ToTitle(scope), $"{hidden} hidden of {list.Count}"));
        }
        CardRepeater.ItemsSource = cards;
    }

    private static string ToTitle(Scope s) => s switch
    {
        Scope.Files       => "Files",
        Scope.Folders     => "Folders",
        Scope.Drives      => "Drives",
        Scope.Background  => "Desktop & folder background",
        _ => s.ToString()
    };

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        var scope = (Scope)btn.Tag;
        Frame.Navigate(typeof(ScopePage), (_vm, scope));
    }
}
