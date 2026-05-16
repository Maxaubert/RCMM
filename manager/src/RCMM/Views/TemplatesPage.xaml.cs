using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;

namespace RCMM.Views;

public sealed partial class TemplatesPage : Page
{
    private NavArgs _args = null!;
    private AddPageViewModel _vm = null!;
    private string _activeChip = "dev";

    public TemplatesPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = (NavArgs)e.Parameter;
        _vm = _args.ViewModel.AddPage
              ?? throw new InvalidOperationException("AddPageViewModel not initialised on MainViewModel");

        ApplyChipStyles();
        RefreshTemplatesList();
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string id)
        {
            _activeChip = id;
            ApplyChipStyles();
            RefreshTemplatesList();
        }
    }

    /// <summary>Active chip gets the lime accent; the rest sit on the surface
    /// color with a subtle border. Geometry (height, padding, corner radius,
    /// font) is owned by ChipButton in App.xaml so this method only flips the
    /// colors per active state.</summary>
    private void ApplyChipStyles()
    {
        foreach (var (chip, id) in new[]
        {
            (ChipDev,     "dev"),
            (ChipProject, "project"),
            (ChipShell,   "shell"),
        })
        {
            bool active = id == _activeChip;
            chip.Background = (Brush)Application.Current.Resources[active ? "AppAccent" : "AppSurface"];
            chip.BorderBrush = (Brush)Application.Current.Resources[active ? "AppAccent" : "AppBorder"];
            chip.Foreground = active
                ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 10, 10, 10))
                : (Brush)Application.Current.Resources["AppText"];
        }
    }

    /// <summary>Ecosystem names that belong to the "Dev tools" chip. The
    /// "Open project" and "Shell" ecosystems each get their own dedicated
    /// chip and are excluded here.</summary>
    private static readonly System.Collections.Generic.HashSet<string> _devEcosystems =
        new() { "Git", "Node", "Python", ".NET", "Rust", "Go" };

    private void RefreshTemplatesList()
    {
        IEnumerable<AdditionTemplates.Template> source = _activeChip switch
        {
            "dev"     => AdditionTemplates.All.Where(t => _devEcosystems.Contains(t.Ecosystem)),
            "project" => AdditionTemplates.All.Where(t => t.Ecosystem == "Open project"),
            "shell"   => AdditionTemplates.All.Where(t => t.Ecosystem == "Shell"),
            _         => Array.Empty<AdditionTemplates.Template>(),
        };
        var grouped = source
            .GroupBy(t => t.Ecosystem)
            .Select(g => new TemplateGroup(g.Key, g))
            .ToList();
        var view = new CollectionViewSource { IsSourceGrouped = true, Source = grouped };
        TemplatesList.ItemsSource = view.View;
        bool hasItems = grouped.Count > 0;
        TemplatesList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility    = hasItems ? Visibility.Collapsed : Visibility.Visible;
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
        if (sender is not Button btn || btn.DataContext is not AdditionTemplates.Template t) return;

        // For "Open in…" templates we resolve the target binary at +Add time:
        //   • Icon = absolute path to the .exe (Windows extracts the icon)
        //   • %bin% in Command substitutes for the resolved path (needed for
        //     Git Bash which isn't on PATH).
        // Falls back to the template's Lucide icon if the binary isn't found.
        string command = t.Command;
        string? icon = t.Icon;
        if (!string.IsNullOrEmpty(t.IconBinary))
        {
            var resolved = BinaryResolver.Find(t.IconBinary!, t.IconBinaryFallbacks);
            if (resolved != null)
            {
                icon = resolved;
                command = command.Replace("%bin%", resolved);
            }
            else
            {
                // No icon, and %bin% will stay literal — the command will fail
                // until the user installs the tool. Better that than silently
                // mapping to the wrong thing.
                command = command.Replace("%bin%", t.IconBinary!);
            }
        }

        var entry = new AdditionEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = t.Name,
            Command = command,
            WorkingDir = t.WorkingDir,
            Scope = t.Scope,
            RunMode = t.RunMode,
            Icon = icon,
        };
        _vm.AddEntry(entry);
        if (Frame.CanGoBack) Frame.GoBack();
    }
}
