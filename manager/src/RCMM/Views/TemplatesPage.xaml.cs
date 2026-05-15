using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Navigation;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;

namespace RCMM.Views;

public sealed partial class TemplatesPage : Page
{
    private NavArgs _args = null!;
    private AddPageViewModel _vm = null!;

    public TemplatesPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = (NavArgs)e.Parameter;
        _vm = _args.ViewModel.AddPage
              ?? throw new InvalidOperationException("AddPageViewModel not initialised on MainViewModel");

        var grouped = AdditionTemplates.All
            .GroupBy(t => t.Ecosystem)
            .Select(g => new TemplateGroup
            {
                Key = g.Key,
                Templates = new ObservableCollection<AdditionTemplates.Template>(g),
            })
            .ToList();
        var view = new CollectionViewSource { IsSourceGrouped = true, Source = grouped };
        TemplatesList.ItemsSource = view.View;
    }

    public sealed class TemplateGroup
    {
        public required string Key { get; init; }
        public required ObservableCollection<AdditionTemplates.Template> Templates { get; init; }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is AdditionTemplates.Template t)
        {
            var entry = new AdditionEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = t.Name,
                Command = t.Command,
                WorkingDir = t.WorkingDir,
                Scope = t.Scope,
                RunMode = t.RunMode,
            };
            _vm.AddEntry(entry);
            if (Frame.CanGoBack) Frame.GoBack();
        }
    }
}
