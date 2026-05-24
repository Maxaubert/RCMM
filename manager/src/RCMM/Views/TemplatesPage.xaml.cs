using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Streams;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;
using RCMM.Util;

namespace RCMM.Views;

public sealed partial class TemplatesPage : Page
{
    private NavArgs _args = null!;
    private AddPageViewModel _vm = null!;
    private string _activeChip = "featured";

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
            (ChipFeatured, "featured"),
            (ChipDev,     "dev"),
            (ChipProject, "project"),
            (ChipShell,   "shell"),
            (ChipFiles,   "files"),
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
        new() { "Git", "Node", "Python", ".NET", "Rust", "Go", "Bun", "pnpm", "uv", "GitHub CLI" };

    private void RefreshTemplatesList()
    {
        List<TemplateGroup> grouped;
        if (_activeChip == "featured")
        {
            // One curated section, in the owner's chosen order — resolve each
            // featured name to its template (skipping any that no longer exist).
            var items = AdditionTemplates.Featured
                .Select(name => AdditionTemplates.All.FirstOrDefault(t => t.Name == name))
                .Where(t => t != null)
                .Select(t => t!)
                .ToList();
            grouped = items.Count > 0
                ? new List<TemplateGroup> { new TemplateGroup("★ Featured", items) }
                : new List<TemplateGroup>();
        }
        else
        {
            IEnumerable<AdditionTemplates.Template> source = _activeChip switch
            {
                "dev"     => AdditionTemplates.All.Where(t => _devEcosystems.Contains(t.Ecosystem)),
                "project" => AdditionTemplates.All.Where(t => t.Ecosystem == "Open project"),
                "shell"   => AdditionTemplates.All.Where(t => t.Ecosystem == "Shell"),
                "files"   => AdditionTemplates.All.Where(t => t.Ecosystem == "Files"),
                _         => Array.Empty<AdditionTemplates.Template>(),
            };
            grouped = source
                .GroupBy(t => t.Ecosystem)
                .Select(g => new TemplateGroup(g.Key, g))
                .ToList();
        }
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

    private void TemplateItem_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AdditionTemplates.Template t) AddTemplate(t);
    }

    private void AddTemplate(AdditionTemplates.Template t)
    {
        var (command, icon) = ExpandTemplate(t);
        // Seed the configured default terminal only for templates that open a visible
        // terminal; a GUI-launch template has no meaningful terminal. (Terminal isn't a
        // hashed field, so this doesn't affect template-update tracking.) null = never
        // chosen → preferred default (Windows Terminal if installed).
        var def = new SettingsStore().Load().DefaultTerminal ?? TerminalCatalog.DefaultPreferred(BinaryResolver.Find);
        string? terminal = !string.IsNullOrWhiteSpace(def) && TerminalCatalog.OpensVisibleTerminal(t.RunMode, command)
            ? def : null;
        var entry = new AdditionEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = t.Name,
            Command = command,
            WorkingDir = t.WorkingDir,
            Scope = t.Scope,
            RunMode = t.RunMode,
            Icon = icon,
            FileTypes = t.FileTypes,
            Terminal = terminal,
        };
        // Stamp so RCMM can later notice if we change this template and offer an update.
        entry = TemplateUpdateService.Stamp(entry, t);
        _vm.AddEntry(entry);
        if (Frame.CanGoBack) Frame.GoBack();
    }

    /// <summary>
    /// Resolve a template's placeholders to the concrete (command, icon) used when
    /// writing an entry. Shared by +Add and the template-update merge so both produce
    /// identical commands:
    ///   • <c>%bin%</c> → the resolved target binary's absolute path (or the literal
    ///     binary name if it can't be found, so the user sees what's missing).
    ///   • <c>%selfdir%</c> → the directory of RCMM.exe (where the shipped scripts live).
    ///   • Icon priority: explicit template.Icon (e.g. "lib:claude") wins; else the
    ///     resolved binary (Windows extracts its .exe icon); else null.
    /// </summary>
    internal static (string command, string? icon) ExpandTemplate(AdditionTemplates.Template t)
    {
        string command = t.Command;
        string? icon = t.Icon;
        if (!string.IsNullOrEmpty(t.IconBinary))
        {
            var resolved = BinaryResolver.Find(t.IconBinary!, t.IconBinaryFallbacks);
            if (resolved != null)
            {
                if (string.IsNullOrEmpty(icon)) icon = resolved;
                command = command.Replace("%bin%", resolved);
            }
            else
            {
                command = command.Replace("%bin%", t.IconBinary!);
            }
        }
        var selfDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath)
                      ?? AppContext.BaseDirectory.TrimEnd('\\');
        command = command.Replace("%selfdir%", selfDir);
        return (command, icon);
    }

    // ---- Template row icons (lib vector, or extracted exe icon) -------------

    private void TplRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Grid g && g.DataContext is AdditionTemplates.Template t) ApplyTemplateRowIcon(g, t);
    }

    private void TplRow_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Grid g && args.NewValue is AdditionTemplates.Template t) ApplyTemplateRowIcon(g, t);
    }

    private void ApplyTemplateRowIcon(Grid g, AdditionTemplates.Template t)
    {
        if (g.FindName("TplIcon") is not Border host) return;
        host.Child = null;
        host.Visibility = Visibility.Collapsed;

        // 1. Library icon (lib:…) — synchronous vector render. Covers Featured,
        //    dev tools, and the AI-CLI launchers.
        if (IconLibrary.IsLibraryName(t.Icon))
        {
            var p = IconRender.BuildIconElement(t.Icon!, 18,
                (Brush)Application.Current.Resources["AppText"], thickness: 1.75);
            if (p != null) { host.Child = p; host.Visibility = Visibility.Visible; }
            return;
        }

        // 2. Binary icon (editors / shells) — resolve the exe and extract its
        //    icon off the UI thread, then paint it back if the row still matches.
        if (!string.IsNullOrEmpty(t.IconBinary))
        {
            var resolved = BinaryResolver.Find(t.IconBinary!, t.IconBinaryFallbacks);
            if (resolved != null) _ = LoadBinaryIconAsync(g, t, resolved);
        }
    }

    private static async Task LoadBinaryIconAsync(Grid g, AdditionTemplates.Template t, string path)
    {
        var bytes = await IconHelper.LoadIconBytesAsync(path);
        if (bytes == null || bytes.Length == 0) return;
        // The container may have been recycled to a different template while we
        // awaited — only paint if this row still shows the same one.
        if (!ReferenceEquals(g.DataContext, t)) return;
        if (g.FindName("TplIcon") is not Border host) return;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var dw = new DataWriter(stream))
            {
                dw.WriteBytes(bytes);
                await dw.StoreAsync();
                await dw.FlushAsync();
                dw.DetachStream();
            }
            stream.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            if (!ReferenceEquals(g.DataContext, t)) return;
            host.Child = new Image { Source = bmp, Width = 18, Height = 18 };
            host.Visibility = Visibility.Visible;
        }
        catch { }
    }
}
