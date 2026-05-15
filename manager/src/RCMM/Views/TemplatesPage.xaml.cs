using System;
using System.Collections.Generic;
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
            .Select(g => new TemplateGroup(g.Key, g))
            .ToList();
        var view = new CollectionViewSource { IsSourceGrouped = true, Source = grouped };
        TemplatesList.ItemsSource = view.View;
    }

    /// <summary>
    /// Group object for the Templates browser. CollectionViewSource's grouping
    /// requires each group to itself be an iterable of items — inheriting from
    /// ObservableCollection&lt;T&gt; gives that for free. Without this, the page
    /// renders empty because WinUI can't enumerate the items inside a group.
    /// </summary>
    public sealed class TemplateGroup : ObservableCollection<AdditionTemplates.Template>
    {
        public string Key { get; }
        public TemplateGroup(string key, IEnumerable<AdditionTemplates.Template> items) : base(items.ToList())
        {
            Key = key;
        }
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
