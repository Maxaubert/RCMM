using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using RCMM.Core.Diagnostics;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;
using RCMM.Util;
using Windows.ApplicationModel.DataTransfer;

namespace RCMM.Views;

/// <summary>
/// Add-to-menu editor. Adaptive 2/3-pane layout matching the brainstorming
/// mockup: left pane lists every entry+folder flattened with twist
/// expand/collapse; middle pane appears when a folder is selected and shows
/// that folder's children (drillable); right pane edits whichever item is
/// selected. Drag-and-drop reorders within a bucket and moves items between
/// buckets via drop-on-folder or drop-into-middle-pane.
///
/// Row rendering is driven by an <see cref="AddRow"/> adapter rebuilt from the
/// view-model on every state change — the list source is replaced wholesale
/// because indent depths and twist glyphs depend on which folders are expanded.
/// </summary>
public sealed partial class AddPage : Page
{
    private const string Cat = "addpage";
    private const string TopLevelLabel = "(top-level)";

    private NavArgs _args = null!;
    private AddPageViewModel _vm = null!;

    private readonly ObservableCollection<AddRow> _leftRows = new();
    private readonly ObservableCollection<AddRow> _middleRows = new();
    private readonly HashSet<string> _expanded = new();
    /// <summary>Stack of folder ids the user has drilled into via the middle pane.</summary>
    private readonly List<string> _middlePath = new();

    private string? _selectedKind; // "entry" | "folder"
    private string? _selectedId;

    // Drag-state — captured on DragItemsStarting; consumed in
    // LeftRows_CollectionChanged (Move action) and MiddleRows_CollectionChanged.
    private string? _dragKind;
    private string? _dragId;
    private string? _dragBucket;

    public AddPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Log.Info(Cat, "AddPage.OnNavigatedTo: entering");
        _args = (NavArgs)e.Parameter;
        _vm = _args.ViewModel.AddPage
              ?? throw new InvalidOperationException("AddPageViewModel not initialised on MainViewModel");
        Log.Info(Cat, $"OnNavigatedTo: vm has {_vm.Entries.Count} entries, {_vm.Folders.Count} folders");

        // Static combo sources
        ScopeBox.ItemsSource    = Enum.GetValues<AdditionScope>().Cast<object>().ToList();
        RunModeBox.ItemsSource  = Enum.GetValues<RunMode>().Cast<object>().ToList();

        LeftList.ItemsSource = _leftRows;
        MiddleList.ItemsSource = _middleRows;

        RebuildLeftRows();
        RebuildMiddleRows();
        RenderEditor();

        // Refresh on every VM mutation. Apply/Discard land here too.
        _vm.Entries.CollectionChanged += (_, ev) => { Log.Info(Cat, $"vm.Entries changed: {ev.Action}"); RefreshAll(); };
        _vm.Folders.CollectionChanged += (_, ev) => { Log.Info(Cat, $"vm.Folders changed: {ev.Action}"); RefreshAll(); };

        // Detect when WinUI's CanReorderItems reorders the row collection. The
        // framework calls Move(oldIdx, newIdx) on the ItemsSource — that's our
        // signal that the user finished a drag, and it's far more reliable
        // than DragItemsCompleted, which in WinUI 3 2.0.x simply doesn't fire
        // after an internal reorder.
        _leftRows.CollectionChanged += LeftRows_CollectionChanged;
        _middleRows.CollectionChanged += MiddleRows_CollectionChanged;
    }

    // CollectionChanged.Move handlers — kept as a safety net for whoever
    // shoves a Move through the row collection. Skipped when the pointer
    // drag is mid-flight (its many Move calls during the drag would otherwise
    // trigger VM persistence and a RefreshAll that yanks the list out from
    // under the user's cursor).
    private void LeftRows_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_suppressMovePersist) return;
        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Move) return;
        Log.Info(Cat, $"_leftRows MOVE old={e.OldStartingIndex} new={e.NewStartingIndex} dragId={_dragId}");
    }

    private void MiddleRows_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_suppressMovePersist) return;
        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Move) return;
        Log.Info(Cat, $"_middleRows MOVE old={e.OldStartingIndex} new={e.NewStartingIndex} dragId={_dragId}");
    }

    private void RefreshAll()
    {
        Log.Info(Cat, $"RefreshAll: triggered (selected kind={_selectedKind} id={_selectedId})");
        RebuildLeftRows();
        ApplySelectionToRows();
        RebuildMiddleRows();
        RenderEditor();
    }

    // -------------------------------------------------------------------------
    // ROW REBUILD — left + middle
    // -------------------------------------------------------------------------

    private void RebuildLeftRows()
    {
        Log.Info(Cat, $"RebuildLeftRows: clearing {_leftRows.Count} rows (vm: {_vm.Entries.Count} entries, {_vm.Folders.Count} folders)");
        _leftRows.Clear();
        void Emit(string? bucketId, int depth)
        {
            // Folders first then entries within the same bucket, matching the
            // mockup's left-list ordering.
            foreach (var f in _vm.Folders.Where(f => (f.ParentFolderId ?? null) == (bucketId ?? null)))
            {
                _leftRows.Add(new AddRow
                {
                    Kind = "folder", Id = f.Id,
                    Bucket = bucketId ?? "root",
                    Indent = depth,
                    Label = f.Name,
                    IconValue = f.Icon,
                    IsExpanded = _expanded.Contains(f.Id),
                    Badge = CountChildren(f.Id) is var n && n > 0 ? n.ToString() : null,
                });
                if (_expanded.Contains(f.Id)) Emit(f.Id, depth + 1);
            }
            foreach (var e in _vm.Entries.Where(e => (e.FolderId ?? null) == (bucketId ?? null)))
            {
                _leftRows.Add(new AddRow
                {
                    Kind = "entry", Id = e.Id,
                    Bucket = bucketId ?? "root",
                    Indent = depth,
                    Label = e.Name,
                    IconValue = e.Icon,
                });
            }
        }
        Emit(null, 0);
    }

    private int CountChildren(string folderId)
    {
        int c = 0;
        foreach (var e in _vm.Entries) if (e.FolderId == folderId) c++;
        foreach (var f in _vm.Folders) if (f.ParentFolderId == folderId) c++;
        return c;
    }

    private void RebuildMiddleRows()
    {
        _middleRows.Clear();
        var folderId = CurrentMiddleFolderId();
        if (folderId == null)
        {
            MiddlePane.Visibility = Visibility.Collapsed;
            MiddleCol.Width = new GridLength(0);
            // Leave LeftCol's width untouched — flipping it between 300 and 340
            // every time the user picks an entry vs a folder causes ListView to
            // reflow and lose scroll position, which the user sees as a
            // "list jumps then resets" glitch on row click.
            return;
        }
        MiddlePane.Visibility = Visibility.Visible;
        MiddleCol.Width = new GridLength(1, GridUnitType.Star);

        var f = _vm.Folders.FirstOrDefault(x => x.Id == folderId);
        if (f == null) { MiddlePane.Visibility = Visibility.Collapsed; return; }

        // Breadcrumbs
        BuildBreadcrumb(f);

        int ord = 0;
        foreach (var sub in _vm.Folders.Where(x => x.ParentFolderId == f.Id))
        {
            ord++;
            _middleRows.Add(new AddRow
            {
                Kind = "folder", Id = sub.Id,
                Bucket = f.Id,
                Indent = 0,
                Label = sub.Name,
                SubText = CountChildren(sub.Id) + " items",
                Ordinal = ord.ToString(),
                IconValue = sub.Icon,
            });
        }
        foreach (var entry in _vm.Entries.Where(x => x.FolderId == f.Id))
        {
            ord++;
            _middleRows.Add(new AddRow
            {
                Kind = "entry", Id = entry.Id,
                Bucket = f.Id,
                Indent = 0,
                Label = entry.Name,
                SubText = entry.Command,
                Ordinal = ord.ToString(),
                IconValue = entry.Icon,
            });
        }
        MiddleCount.Text = (ord == 1 ? "1 item" : ord + " items");
        BackButton.IsEnabled = _middlePath.Count > 0;
    }

    private void BuildBreadcrumb(AdditionFolder current)
    {
        CrumbStack.Children.Clear();
        var chain = new List<AdditionFolder>();
        var cur = current;
        int safety = 0;
        while (cur != null && ++safety < 64)
        {
            chain.Insert(0, cur);
            cur = cur.ParentFolderId == null ? null : _vm.Folders.FirstOrDefault(f => f.Id == cur.ParentFolderId);
        }
        for (int i = 0; i < chain.Count; i++)
        {
            if (i > 0)
            {
                var sep = new TextBlock
                {
                    Text = "›",
                    Foreground = (Brush)Application.Current.Resources["AppTextDim"],
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                CrumbStack.Children.Add(sep);
            }
            var ancestor = chain[i];
            bool isLast = i == chain.Count - 1;
            var tb = new TextBlock
            {
                Text = ancestor.Name,
                Foreground = isLast
                    ? (Brush)Application.Current.Resources["AppText"]
                    : (Brush)Application.Current.Resources["AppTextMuted"],
                FontSize = 14,
                FontWeight = isLast ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (!isLast)
            {
                tb.IsTapEnabled = true;
                var anchorId = ancestor.Id;
                tb.Tapped += (_, __) =>
                {
                    // Pop middlePath until anchorId is at top (or empty + select).
                    while (_middlePath.Count > 0 && _middlePath[^1] != anchorId)
                        _middlePath.RemoveAt(_middlePath.Count - 1);
                    if (_middlePath.Count == 0) { _selectedKind = "folder"; _selectedId = anchorId; }
                    RefreshAll();
                };
            }
            CrumbStack.Children.Add(tb);
        }
    }

    private string? CurrentMiddleFolderId()
    {
        if (_middlePath.Count > 0) return _middlePath[^1];
        return _selectedKind == "folder" ? _selectedId : null;
    }

    // -------------------------------------------------------------------------
    // ROW VISUAL HOOKS — per-row Loaded handler to paint indent/twist/icon
    // -------------------------------------------------------------------------

    /// <summary>Fires when a left-pane row's content Grid is realised — initial
    /// container mount. Paints every visual from the AddRow.</summary>
    private void RowRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid g || g.DataContext is not AddRow r) return;
        ApplyLeftRowVisuals(g, r);
    }

    /// <summary>Fires when WinUI recycles a container onto a different AddRow
    /// (virtualization). Without this, recycled rows keep their previous visuals
    /// — the root cause of the original "highlight stuck on wrong row" bug.</summary>
    private void RowRoot_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Grid g && args.NewValue is AddRow r) ApplyLeftRowVisuals(g, r);
    }

    /// <summary>Paint every visual of a left-pane row from its AddRow + current
    /// selection. Driven from code (not XAML bindings) because INPC bindings
    /// under WinUI 3 virtualization left stale subscriptions across container
    /// recycles, which caused the SelBar to appear on the wrong rows.</summary>
    private const double IndentUnit = 28.0;

    private void ApplyLeftRowVisuals(Grid g, AddRow r)
    {
        if (g.FindName("IndentSpacer") is Border indent)
            indent.Width = r.Indent * IndentUnit;
        // Vertical guide rail at the midpoint of the deepest indent unit, so
        // an indented row visually "branches" off the column where its parent
        // folder sits. Hidden on root rows.
        if (g.FindName("IndentGuide") is Microsoft.UI.Xaml.Shapes.Rectangle guide)
        {
            if (r.Indent > 0)
            {
                guide.Margin = new Thickness((r.Indent - 1) * IndentUnit + IndentUnit / 2, 0, 0, 0);
                guide.Visibility = Visibility.Visible;
            }
            else
            {
                guide.Visibility = Visibility.Collapsed;
            }
        }
        if (g.FindName("SelBar") is Microsoft.UI.Xaml.Shapes.Rectangle selBar)
            selBar.Visibility = (r.Kind == _selectedKind && r.Id == _selectedId) ? Visibility.Visible : Visibility.Collapsed;
        if (g.FindName("Twist") is TextBlock twist)
            twist.Text = r.TwistGlyph;
        if (g.FindName("Label") is TextBlock label)
            label.Text = r.Label;
        if (g.FindName("Badge") is TextBlock badge)
        {
            badge.Text = r.Badge ?? "";
            badge.Visibility = string.IsNullOrEmpty(r.Badge) ? Visibility.Collapsed : Visibility.Visible;
        }
        // Icon — fresh Path per row (Geometry can't be shared across Paths).
        if (g.FindName("IconHost") is Border iconHost)
        {
            if (IconLibrary.IsLibraryName(r.IconValue))
            {
                var p = IconRender.BuildIconElement(r.IconValue!, 16,
                    (Brush)Application.Current.Resources["AppTextMuted"], thickness: 1.75);
                if (p != null) { iconHost.Child = p; iconHost.Visibility = Visibility.Visible; }
                else { iconHost.Child = null; iconHost.Visibility = Visibility.Collapsed; }
            }
            else { iconHost.Child = null; iconHost.Visibility = Visibility.Collapsed; }
        }
    }

    private void MidRowRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid g || g.DataContext is not AddRow r) return;
        ApplyMidRowVisuals(g, r);
    }

    private void MidRowRoot_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Grid g && args.NewValue is AddRow r) ApplyMidRowVisuals(g, r);
    }

    private void ApplyMidRowVisuals(Grid g, AddRow r)
    {
        if (g.FindName("MidOrder") is TextBlock ord) ord.Text = r.Ordinal ?? "";
        if (g.FindName("MidLabel") is TextBlock lab) lab.Text = r.Label;
        if (g.FindName("MidSub") is TextBlock sub)
        {
            sub.Text = r.SubText ?? "";
            sub.Visibility = string.IsNullOrEmpty(r.SubText) ? Visibility.Collapsed : Visibility.Visible;
        }
        if (g.FindName("MidIconHost") is Border iconHost)
        {
            if (IconLibrary.IsLibraryName(r.IconValue))
            {
                var p = IconRender.BuildIconElement(r.IconValue!, 20,
                    (Brush)Application.Current.Resources["AppTextMuted"], thickness: 1.75);
                if (p != null) { iconHost.Child = p; iconHost.Visibility = Visibility.Visible; }
                else { iconHost.Child = null; iconHost.Visibility = Visibility.Collapsed; }
            }
            else { iconHost.Child = null; iconHost.Visibility = Visibility.Collapsed; }
        }
    }

    // -------------------------------------------------------------------------
    // SELECTION + EXPAND
    // -------------------------------------------------------------------------

    private void LeftList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Log.Info(Cat, $"LeftList_SelectionChanged added={e.AddedItems.Count} removed={e.RemovedItems.Count} selected={(LeftList.SelectedItem as AddRow)?.Id ?? "<null>"}");
        if (LeftList.SelectedItem is not AddRow row)
        {
            Log.Info(Cat, "LeftList_SelectionChanged: SelectedItem isn't AddRow, returning");
            return;
        }
        _selectedKind = row.Kind;
        _selectedId = row.Id;
        _middlePath.Clear();
        ApplySelectionToRows();
        RebuildMiddleRows();
        RenderEditor();
        Log.Info(Cat, $"LeftList_SelectionChanged: end selectedKind={_selectedKind} selectedId={_selectedId}");
    }

    /// <summary>
    /// Refresh the SelBar visibility on every realized container. We walk the
    /// containers directly instead of going through INPC + binding because
    /// WinUI 3 ListView container recycling under {Binding} kept stale INPC
    /// subscriptions, which caused SelBar to appear on the wrong rows.
    /// </summary>
    private void ApplySelectionToRows()
    {
        int matched = 0;
        for (int i = 0; i < _leftRows.Count; i++)
        {
            var r = _leftRows[i];
            bool sel = r.Kind == _selectedKind && r.Id == _selectedId;
            if (sel) matched++;
            if (LeftList.ContainerFromIndex(i) is ListViewItem container)
            {
                var rowRoot = FindDescendantByName(container, "RowRoot") as Grid;
                if (rowRoot?.FindName("SelBar") is Microsoft.UI.Xaml.Shapes.Rectangle bar)
                    bar.Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        for (int i = 0; i < _middleRows.Count; i++)
        {
            // Middle pane has no SelBar (selection is implicit there), but we
            // still iterate for parity in case we add one later.
        }
        Log.Debug(Cat, $"ApplySelectionToRows: {matched} row(s) marked selected in left list");
    }

    private static DependencyObject? FindDescendantByName(DependencyObject root, string name)
    {
        if (root is FrameworkElement fe && fe.Name == name) return root;
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            var hit = FindDescendantByName(child, name);
            if (hit != null) return hit;
        }
        return null;
    }

    private void MiddleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MiddleList.SelectedItem is not AddRow row) return;
        if (row.Kind == "folder")
        {
            _middlePath.Add(row.Id);
            RebuildMiddleRows();
            RenderEditor();
            MiddleList.SelectedItem = null;
        }
        else
        {
            _selectedKind = "entry"; _selectedId = row.Id;
            _middlePath.Clear();
            RebuildLeftRows();
            ApplySelectionToRows();
            RenderEditor();
        }
    }

    private void Twist_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AddRow r && r.Kind == "folder")
        {
            if (_expanded.Contains(r.Id)) _expanded.Remove(r.Id);
            else _expanded.Add(r.Id);
            RebuildLeftRows();
            e.Handled = true;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_middlePath.Count == 0) return;
        _middlePath.RemoveAt(_middlePath.Count - 1);
        RebuildMiddleRows();
        RenderEditor();
    }

    // -------------------------------------------------------------------------
    // TOOLBAR
    // -------------------------------------------------------------------------

    private void NewEntry_Click(object sender, RoutedEventArgs e)
    {
        var entry = new AdditionEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "New entry",
            Command = "",
            WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground,
            RunMode = RunMode.VisibleTerminal,
        };
        _vm.AddEntry(entry);
        SelectAndEdit("entry", entry.Id);
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = new AdditionFolder { Id = Guid.NewGuid().ToString("N"), Name = "New folder" };
        _vm.AddFolder(folder);
        SelectAndEdit("folder", folder.Id);
    }

    private void Templates_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(TemplatesPage), _args);
    }

    private void SelectAndEdit(string kind, string id)
    {
        _selectedKind = kind; _selectedId = id; _middlePath.Clear();
        RebuildLeftRows(); RebuildMiddleRows(); RenderEditor();
        // Try to select in the ListView so the highlight syncs
        var row = _leftRows.FirstOrDefault(r => r.Kind == kind && r.Id == id);
        if (row != null) LeftList.SelectedItem = row;
    }

    // -------------------------------------------------------------------------
    // EDITOR
    // -------------------------------------------------------------------------

    private bool _suppressFieldChange;

    private void RenderEditor()
    {
        if (_selectedKind == "entry" && _vm.Entries.FirstOrDefault(e => e.Id == _selectedId) is { } entry)
            ShowEntryEditor(entry);
        else if (_selectedKind == "folder" || CurrentMiddleFolderId() != null)
        {
            var fid = CurrentMiddleFolderId() ?? _selectedId;
            if (fid != null && _vm.Folders.FirstOrDefault(f => f.Id == fid) is { } folder)
                ShowFolderEditor(folder);
            else { EmptyEditor(); }
        }
        else EmptyEditor();
    }

    private void EmptyEditor()
    {
        EditorPanel.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void ShowEntryEditor(AdditionEntry entry)
    {
        EmptyState.Visibility = Visibility.Collapsed;
        EditorPanel.Visibility = Visibility.Visible;
        EditorTitle.Text = "Edit entry";
        SetEntryFieldsVisibility(true);
        FolderInfoRow.Visibility = Visibility.Collapsed;

        _suppressFieldChange = true;
        try
        {
            NameBox.Text = entry.Name;
            CommandBox.Text = entry.Command;
            WorkingDirBox.Text = entry.WorkingDir;
            FileTypesBox.Text = entry.FileTypes is { Count: > 0 } ? string.Join(", ", entry.FileTypes) : "";
            ScopeBox.SelectedItem = entry.Scope;
            RunModeBox.SelectedItem = entry.RunMode;
            SetupTerminalRow(entry.RunMode, entry.Command, entry.Terminal);

            var folderOptions = new List<object> { TopLevelLabel };
            foreach (var f in _vm.Folders) folderOptions.Add(f);
            FolderBox.ItemsSource = folderOptions;
            FolderBox.SelectedItem = entry.FolderId == null
                ? (object)TopLevelLabel
                : _vm.Folders.FirstOrDefault(f => f.Id == entry.FolderId) ?? (object)TopLevelLabel;

            RenderIconPicker(entry.Icon);
        }
        finally { _suppressFieldChange = false; }
    }

    private void ShowFolderEditor(AdditionFolder folder)
    {
        EmptyState.Visibility = Visibility.Collapsed;
        EditorPanel.Visibility = Visibility.Visible;
        EditorTitle.Text = "Edit folder";
        SetEntryFieldsVisibility(false);
        ParentFolderRow.Visibility = Visibility.Visible;
        ScopeRow.Visibility = Visibility.Visible;
        FolderInfoRow.Visibility = Visibility.Visible;

        _suppressFieldChange = true;
        try
        {
            NameBox.Text = folder.Name;
            ScopeBox.SelectedItem = folder.Scope;

            // Parent folder dropdown — exclude self + descendants
            var parentOptions = new List<object> { TopLevelLabel };
            foreach (var f in _vm.Folders)
            {
                if (f.Id == folder.Id) continue;
                if (IsDescendant(f.Id, folder.Id)) continue;
                parentOptions.Add(f);
            }
            ParentFolderBox.ItemsSource = parentOptions;
            ParentFolderBox.SelectedItem = folder.ParentFolderId == null
                ? (object)TopLevelLabel
                : _vm.Folders.FirstOrDefault(f => f.Id == folder.ParentFolderId) ?? (object)TopLevelLabel;

            RenderIconPicker(folder.Icon);

            FolderInfoText.Text = $"Level {_vm.FolderDepth(folder.Id)} of {AddPageViewModel.MaxFolderDepth} · {CountChildren(folder.Id)} items inside";
        }
        finally { _suppressFieldChange = false; }
    }

    private bool IsDescendant(string maybeDescendant, string ancestorId)
    {
        var cur = _vm.Folders.FirstOrDefault(f => f.Id == maybeDescendant);
        int hops = 0;
        while (cur != null && ++hops < 64)
        {
            if (cur.ParentFolderId == ancestorId) return true;
            cur = _vm.Folders.FirstOrDefault(f => f.Id == cur.ParentFolderId);
        }
        return false;
    }

    private void SetEntryFieldsVisibility(bool showEntryFields)
    {
        var vis = showEntryFields ? Visibility.Visible : Visibility.Collapsed;
        CommandRow.Visibility = vis;
        WorkRunRow.Visibility = vis;
        FileTypesRow.Visibility = vis;
        FolderRow.Visibility = vis;
        ParentFolderRow.Visibility = showEntryFields ? Visibility.Collapsed : Visibility.Visible;
        // The Terminal row is only meaningful for entries that open a console;
        // SetupTerminalRow (called per entry) decides whether to show it. Folders
        // never have one.
        if (!showEntryFields) TerminalRow.Visibility = Visibility.Collapsed;
    }

    private void RenderIconPicker(string? iconValue)
    {
        IconPreviewCell.Children.Clear();
        IconCustomBox.Text = "";
        if (IconLibrary.IsLibraryName(iconValue))
        {
            // Build a fresh Path (with its own Geometry) — sharing a cached
            // Geometry across multiple Paths throws E_INVALIDARG on the second
            // consumer because a Geometry can only have one parent.
            var fresh = IconRender.BuildIconElement(iconValue!, 24,
                (Brush)Application.Current.Resources["AppText"], thickness: 2);
            if (fresh != null) IconPreviewCell.Children.Add(fresh);
            IconLabelText.Text = IconLibrary.StripPrefix(iconValue);
            IconSubText.Text = "library icon";
            IconPickButton.Content = "Change icon";
        }
        else if (!string.IsNullOrWhiteSpace(iconValue))
        {
            var tb = new TextBlock { Text = "…", Foreground = (Brush)Application.Current.Resources["AppTextDim"], FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            IconPreviewCell.Children.Add(tb);
            IconLabelText.Text = iconValue;
            IconSubText.Text = "custom path";
            IconPickButton.Content = "Change icon";
            IconCustomBox.Text = iconValue;
        }
        else
        {
            var tb = new TextBlock { Text = "∅", Foreground = (Brush)Application.Current.Resources["AppTextDim"], FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            IconPreviewCell.Children.Add(tb);
            IconLabelText.Text = "No icon";
            IconSubText.Text = "— pick one or leave blank";
            IconPickButton.Content = "Choose icon";
        }
    }

    private void Field_Changed(object sender, RoutedEventArgs e) => SaveCurrent();
    private void Field_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveCurrent();

    /// <summary>Populate + select the Terminal dropdown for an entry, and show it
    /// only when the entry opens a visible terminal. Caller suppresses field-change
    /// events around this (it mutates the ComboBox).</summary>
    private void SetupTerminalRow(RunMode mode, string command, string? terminal)
    {
        bool opens = TerminalCatalog.OpensVisibleTerminal(mode, command);
        TerminalRow.Visibility = opens ? Visibility.Visible : Visibility.Collapsed;
        if (!opens) { TerminalCustomBox.Visibility = Visibility.Collapsed; return; }

        var opts = TerminalCatalog.OptionsFor(mode, BinaryResolver.Find);
        TerminalBox.ItemsSource = opts;

        var stored = string.IsNullOrWhiteSpace(terminal) ? "" : terminal!.Trim();
        var match = opts.FirstOrDefault(o => o.Value == stored);
        if (match != null)
        {
            TerminalBox.SelectedItem = match;
            TerminalCustomBox.Text = "";
            TerminalCustomBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            // A custom path (or a key not available on this PC) → Custom + textbox.
            TerminalBox.SelectedItem = opts.FirstOrDefault(o => o.Value == TerminalCatalog.Custom);
            TerminalCustomBox.Text = stored;
            TerminalCustomBox.Visibility = Visibility.Visible;
        }
    }

    private void Terminal_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFieldChange) return;
        bool custom = (TerminalBox.SelectedItem as TerminalCatalog.Option)?.Value == TerminalCatalog.Custom;
        TerminalCustomBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        SaveCurrent();
    }

    private void RunMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFieldChange) return;
        SaveCurrent();
        // Run mode flips which terminals are valid (shells vs host-only) and
        // whether the row shows at all — refresh it against the saved entry.
        if (_selectedKind == "entry" && _vm.Entries.FirstOrDefault(x => x.Id == _selectedId) is { } entry)
        {
            _suppressFieldChange = true;
            try { SetupTerminalRow(entry.RunMode, entry.Command, entry.Terminal); }
            finally { _suppressFieldChange = false; }
        }
    }

    /// <summary>Current Terminal value from the editor, or the entry's existing
    /// value when the row isn't shown.</summary>
    private string? ReadTerminal(string? fallback)
    {
        if (TerminalRow.Visibility != Visibility.Visible) return fallback;
        if (TerminalBox.SelectedItem is not TerminalCatalog.Option opt) return fallback;
        if (opt.Value == TerminalCatalog.Custom)
            return string.IsNullOrWhiteSpace(TerminalCustomBox.Text) ? null : TerminalCustomBox.Text.Trim();
        return string.IsNullOrEmpty(opt.Value) ? null : opt.Value;
    }
    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Live-update label as the user types. WinUI 3's TextChanged event for
        // TextBox can fire AFTER our _suppressFieldChange flag has been reset,
        // so we can't rely on the flag alone — also short-circuit when the
        // current text already matches what the record holds.
        if (_suppressFieldChange) return;
        if (_selectedKind == "entry" && _vm.Entries.FirstOrDefault(x => x.Id == _selectedId) is { } entry)
        {
            if (entry.Name == NameBox.Text) return;
            _vm.ReplaceEntry(entry with { Name = NameBox.Text });
        }
        else if ((_selectedKind == "folder" || CurrentMiddleFolderId() != null))
        {
            var fid = CurrentMiddleFolderId() ?? _selectedId;
            if (fid != null && _vm.Folders.FirstOrDefault(f => f.Id == fid) is { } folder)
            {
                if (folder.Name == NameBox.Text) return;
                _vm.ReplaceFolder(folder with { Name = NameBox.Text });
            }
        }
    }

    private void SaveCurrent()
    {
        // Same async-event hazard as NameBox_TextChanged: ComboBox.SelectionChanged
        // and TextBox.LostFocus can fire after _suppressFieldChange has been reset.
        // Compute the new record and short-circuit when it equals the existing one —
        // records have value equality, so an unchanged editor never triggers a
        // Replace and the spurious vm.Entries Replace→RefreshAll→RebuildLeftRows
        // cascade that was wiping the list mid-interaction.
        if (_suppressFieldChange) return;
        if (_selectedKind == "entry" && _vm.Entries.FirstOrDefault(x => x.Id == _selectedId) is { } entry)
        {
            var newFolderId = FolderBox.SelectedItem is AdditionFolder f ? f.Id : null;
            var updated = entry with
            {
                Name = NameBox.Text,
                Command = CommandBox.Text,
                WorkingDir = WorkingDirBox.Text,
                Scope = ScopeBox.SelectedItem is AdditionScope s ? s : AdditionScope.FolderBackground,
                RunMode = RunModeBox.SelectedItem is RunMode r ? r : RunMode.VisibleTerminal,
                FileTypes = string.IsNullOrWhiteSpace(FileTypesBox.Text)
                    ? null
                    : FileTypesBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                FolderId = newFolderId,
                Terminal = ReadTerminal(entry.Terminal),
            };
            if (RecordsEffectivelyEqual(entry, updated)) return;
            _vm.ReplaceEntry(updated);
            if ((entry.FolderId ?? null) != (newFolderId ?? null))
                _vm.MoveEntry(entry.Id, newFolderId);
        }
        else
        {
            var fid = CurrentMiddleFolderId() ?? _selectedId;
            if (fid != null && _vm.Folders.FirstOrDefault(f => f.Id == fid) is { } folder)
            {
                var newParent = ParentFolderBox.SelectedItem is AdditionFolder p ? p.Id : null;
                var updated = folder with
                {
                    Name = NameBox.Text,
                    Scope = ScopeBox.SelectedItem is AdditionScope s ? s : AdditionScope.FolderBackground,
                };
                if (folder == updated && (folder.ParentFolderId ?? null) == (newParent ?? null)) return;
                if (folder != updated) _vm.ReplaceFolder(updated);
                if ((folder.ParentFolderId ?? null) != (newParent ?? null))
                {
                    if (!_vm.MoveFolder(folder.Id, newParent))
                    {
                        Log.Warn(Cat, $"folder move refused: depth cap or cycle (id={folder.Id} new parent={newParent})");
                        ParentFolderBox.SelectedItem = folder.ParentFolderId == null
                            ? (object)TopLevelLabel
                            : _vm.Folders.FirstOrDefault(x => x.Id == folder.ParentFolderId) ?? (object)TopLevelLabel;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Record-equality check that also normalises <see cref="AdditionEntry.FileTypes"/>
    /// (record equality on IReadOnlyList compares references, so a freshly-parsed
    /// list would never match an existing-but-identical list).
    /// </summary>
    private static bool RecordsEffectivelyEqual(AdditionEntry a, AdditionEntry b)
    {
        if (a.Name != b.Name) return false;
        if (a.Command != b.Command) return false;
        if (a.WorkingDir != b.WorkingDir) return false;
        if (a.Scope != b.Scope) return false;
        if (a.RunMode != b.RunMode) return false;
        if ((a.Terminal ?? "") != (b.Terminal ?? "")) return false;
        if ((a.Icon ?? "") != (b.Icon ?? "")) return false;
        if ((a.FolderId ?? "") != (b.FolderId ?? "")) return false;
        var fa = a.FileTypes ?? Array.Empty<string>();
        var fb = b.FileTypes ?? Array.Empty<string>();
        if (fa.Count != fb.Count) return false;
        for (int i = 0; i < fa.Count; i++) if (fa[i] != fb[i]) return false;
        return true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedKind == "entry" && _selectedId != null)
        {
            _vm.DeleteEntry(_selectedId);
            _selectedKind = null; _selectedId = null;
        }
        else
        {
            var fid = CurrentMiddleFolderId() ?? _selectedId;
            if (fid != null) _vm.DeleteFolder(fid);
            _selectedKind = null; _selectedId = null; _middlePath.Clear();
        }
        EmptyEditor();
    }

    // -------------------------------------------------------------------------
    // ICON PICKER
    // -------------------------------------------------------------------------

    private async void IconPick_Click(object sender, RoutedEventArgs e)
    {
        // Instrument every step so a crash narrows to a specific line in the log.
        Log.Info(Cat, "IconPick_Click: start");
        try
        {
            var current = CurrentIconValue();
            Log.Info(Cat, $"IconPick_Click: current='{current}', XamlRoot={(this.XamlRoot != null)}");
            IconPickerDialog dialog;
            try
            {
                dialog = new IconPickerDialog(current);
                Log.Info(Cat, "IconPick_Click: dialog ctor OK");
            }
            catch (Exception ex)
            {
                Log.Error(Cat, "IconPick_Click: dialog ctor threw", ex);
                return;
            }
            try { dialog.XamlRoot = this.XamlRoot; Log.Info(Cat, "IconPick_Click: XamlRoot assigned"); }
            catch (Exception ex) { Log.Error(Cat, "IconPick_Click: XamlRoot assign threw", ex); return; }

            try
            {
                Log.Info(Cat, "IconPick_Click: about to ShowAsync");
                await dialog.ShowAsync();
                Log.Info(Cat, $"IconPick_Click: ShowAsync returned, picked='{dialog.PickedValue}'");
            }
            catch (Exception ex) { Log.Error(Cat, "IconPick_Click: ShowAsync threw", ex); return; }

            if (dialog.PickedValue != null && dialog.PickedValue != current)
                SetCurrentIcon(dialog.PickedValue);
        }
        catch (Exception ex)
        {
            Log.Error(Cat, "IconPick_Click: outer", ex);
        }
    }

    private void IconClear_Click(object sender, RoutedEventArgs e) => SetCurrentIcon(null);

    private void IconCustom_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressFieldChange) return;
        var v = IconCustomBox.Text?.Trim();
        if (string.IsNullOrEmpty(v))
        {
            // Clear only if current was a custom path (don't wipe a library pick).
            if (CurrentIconValue() is { } cur && !IconLibrary.IsLibraryName(cur))
                SetCurrentIcon(null);
        }
        else if (!IconLibrary.IsLibraryName(v))
        {
            SetCurrentIcon(v);
        }
    }

    private string? CurrentIconValue()
    {
        if (_selectedKind == "entry" && _vm.Entries.FirstOrDefault(x => x.Id == _selectedId) is { } e) return e.Icon;
        var fid = CurrentMiddleFolderId() ?? _selectedId;
        if (fid != null && _vm.Folders.FirstOrDefault(f => f.Id == fid) is { } f) return f.Icon;
        return null;
    }

    private void SetCurrentIcon(string? icon)
    {
        if (_selectedKind == "entry" && _vm.Entries.FirstOrDefault(x => x.Id == _selectedId) is { } e)
            _vm.ReplaceEntry(e with { Icon = icon });
        else
        {
            var fid = CurrentMiddleFolderId() ?? _selectedId;
            if (fid != null && _vm.Folders.FirstOrDefault(f => f.Id == fid) is { } f)
                _vm.ReplaceFolder(f with { Icon = icon });
        }
        // Repaint preview + row icons
        RenderIconPicker(icon);
    }

    // -------------------------------------------------------------------------
    // DRAG & DROP — manual pointer-driven reorder
    // -------------------------------------------------------------------------
    //
    // We tried every flavour of WinUI 3 built-in drag and drop:
    //   • ListView.CanReorderItems + CanDragItems → fires DragItemsStarting
    //     but the framework's internal drop never completes, no Move event
    //     on the ItemsSource, no DragItemsCompleted callback.
    //   • CanDragItems + AllowDrop + custom DragOver/Drop → DragItemsStarting
    //     fires, then no DragOver follows (same-ListView self-drop refused).
    //   • CanDrag="True" on a child Border (per-row drag handle) → no
    //     DragStarting ever fires; ListViewItem swallows the pointer first.
    //
    // Root cause: unpackaged WinUI 3 apps (WindowsPackageType=None) don't get
    // the OS-level drag/drop infrastructure (IDropTargetHelper et al.) COM-
    // registered, so the framework's StartDragAsync produces no useful events
    // even though DragItemsStarting fires.
    //
    // Implementation: bypass all of that. Each row has a DragHandle grip on
    // the right (custom Border subclass that sets a 4-way move cursor on
    // hover). PointerPressed on the grip captures the pointer; PointerMoved
    // hit-tests sibling rows and live-Moves the dragged row in the
    // ObservableCollection (the ListView animates the swap); PointerReleased
    // persists the final position to the view-model.

    private enum DropZone { None, Above, Below, Into, IntoMiddlePane }

    private AddRow? _gripRow;
    private ListView? _gripList;                             // source list
    private ListView? _gripTargetList;                       // list under cursor (may differ from source)
    private bool _gripActive;
    private Windows.Foundation.Point _gripPressOrigin;       // source-list-local
    private Windows.Foundation.Point _gripPressCanvasOrigin; // Canvas-local
    private double _ghostOffsetX;                            // cursor → ghost top-left
    private double _ghostOffsetY;
    private DropZone _gripTargetZone = DropZone.None;
    private int _gripTargetSlot = -1;                        // insertion slot (above/below)
    private AddRow? _gripTargetRow;                          // for "into folder" zone
    private FrameworkElement? _gripSourceContainer;          // dimmed during drag
    private bool _suppressMovePersist;

    /// <summary>Press on a row grip. Captures the pointer on the Page so the
    /// ListView's internal ScrollViewer can't steal capture for pan detection.
    /// Sets up but doesn't show the drag visuals — those appear once the user
    /// moves past the drag threshold.</summary>
    private void GripHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not AddRow row) { Log.Warn(Cat, "GripHost_PointerPressed: no AddRow"); return; }
        var list = FindAncestorListView(fe);
        if (list == null) { Log.Warn(Cat, "GripHost_PointerPressed: no ancestor ListView"); return; }

        _gripRow = row; _gripList = list;
        _gripActive = false;
        _gripTargetZone = DropZone.None;
        _gripTargetSlot = -1;
        _gripTargetRow = null;
        _gripPressOrigin = e.GetCurrentPoint(list).Position;
        _gripPressCanvasOrigin = e.GetCurrentPoint(DragOverlay).Position;

        // Offset such that the ghost trails slightly down-right of the cursor.
        _ghostOffsetX = 14;
        _ghostOffsetY = 8;

        // Capture on the PAGE — not on the grip — so the ScrollViewer inside
        // the ListView can't claim capture for pan detection.
        bool ok = this.CapturePointer(e.Pointer);
        Log.Info(Cat, $"GripHost_PointerPressed kind={row.Kind} id={row.Id} bucket={row.Bucket} list={(ReferenceEquals(list, LeftList) ? "left" : "middle")} pageCapture={ok}");
        e.Handled = true;
    }

    private void Page_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_gripRow == null || _gripList == null) return;
        var sourcePos = e.GetCurrentPoint(_gripList).Position;
        var dy = sourcePos.Y - _gripPressOrigin.Y;
        if (!_gripActive && Math.Abs(dy) < 4) return;
        if (!_gripActive)
        {
            _gripActive = true;
            Log.Info(Cat, $"drag active threshold crossed dy={dy:F1}");
            BeginDragVisuals(_gripRow, _gripList);
        }

        // Ghost follows the cursor in Canvas-local coords.
        var canvasPos = e.GetCurrentPoint(DragOverlay).Position;
        Canvas.SetLeft(DragGhost, canvasPos.X + _ghostOffsetX);
        Canvas.SetTop(DragGhost,  canvasPos.Y + _ghostOffsetY);

        // Re-resolve the target list every move — the user can drag from the
        // left list into the middle pane (or vice-versa).
        var pagePos = e.GetCurrentPoint(this).Position;
        var targetList = CurrentTargetList(pagePos);
        _gripTargetList = targetList;

        DropZone zone; int slot; AddRow? targetRow;
        if (targetList == null)
        {
            // Cursor outside any list: clear indicators but keep ghost.
            zone = DropZone.None; slot = -1; targetRow = null;
        }
        else
        {
            var listPos = e.GetCurrentPoint(targetList).Position;
            var rows = ReferenceEquals(targetList, LeftList) ? _leftRows : _middleRows;
            (zone, slot, targetRow) = ComputeDropTarget(targetList, rows, listPos.Y, _gripRow);

            // Middle pane: "off any row but inside the pane" → drop into the
            // current middle folder. Likewise, dragging in from the side: a
            // middle pane with zero rows always means IntoMiddlePane.
            if (ReferenceEquals(targetList, MiddleList) && zone != DropZone.Into)
            {
                bool hitNothing = targetRow == null && _middleRows.Count == 0;
                if (hitNothing || OutsideRowsButInPane(MiddleList, listPos.Y))
                {
                    var folderId = CurrentMiddleFolderId();
                    if (folderId != null && _gripRow.Id != folderId)
                    {
                        zone = DropZone.IntoMiddlePane; slot = -1; targetRow = null;
                    }
                }
            }
        }

        if (zone != _gripTargetZone || slot != _gripTargetSlot || !ReferenceEquals(targetRow, _gripTargetRow))
        {
            _gripTargetZone = zone; _gripTargetSlot = slot; _gripTargetRow = targetRow;
            PositionDropTarget(targetList, zone, slot, targetRow);
        }
    }

    /// <summary>
    /// Hit-test the cursor (in Page coords) against the visible lists. Middle
    /// pane wins when visible and hit — that's the user-facing intent of
    /// "drag onto the open folder's contents".
    /// </summary>
    private ListView? CurrentTargetList(Windows.Foundation.Point pagePos)
    {
        if (MiddlePane.Visibility == Visibility.Visible)
        {
            var mb = MiddlePane.TransformToVisual(this).TransformBounds(
                new Windows.Foundation.Rect(0, 0, MiddlePane.ActualWidth, MiddlePane.ActualHeight));
            if (pagePos.X >= mb.Left && pagePos.X <= mb.Right) return MiddleList;
        }
        var lb = LeftList.TransformToVisual(this).TransformBounds(
            new Windows.Foundation.Rect(0, 0, LeftList.ActualWidth, LeftList.ActualHeight));
        if (pagePos.X >= lb.Left && pagePos.X <= lb.Right) return LeftList;
        return null;
    }

    private static bool OutsideRowsButInPane(ListView list, double y)
    {
        // True if y is past the last row of the list (i.e. the cursor is in
        // the pane's empty area below the items). list.ActualHeight is the
        // pane's full height in its own coord space, so it's a safe bound.
        for (int i = list.Items.Count - 1; i >= 0; i--)
        {
            if (list.ContainerFromIndex(i) is not FrameworkElement c) continue;
            var b = c.TransformToVisual(list).TransformBounds(new Windows.Foundation.Rect(0, 0, c.ActualWidth, c.ActualHeight));
            return y > b.Bottom;
        }
        return true; // no rows realised → pane is empty
    }

    private void Page_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        bool wasActive = _gripActive;
        var row = _gripRow;
        var sourceList = _gripList;
        var targetList = _gripTargetList;
        var zone = _gripTargetZone;
        int slot = _gripTargetSlot;
        var intoFolder = _gripTargetRow;
        this.ReleasePointerCapture(e.Pointer);
        EndDragVisuals();
        _gripRow = null; _gripList = null; _gripTargetList = null; _gripActive = false;
        _gripTargetZone = DropZone.None; _gripTargetSlot = -1; _gripTargetRow = null;

        if (!wasActive || row == null || sourceList == null)
        {
            Log.Info(Cat, $"Page_PointerReleased wasActive={wasActive} row={row?.Id ?? "<null>"}");
            return;
        }

        // Drop INTO a folder row (cursor on the middle 50% of a folder).
        if (zone == DropZone.Into && intoFolder != null && intoFolder.Kind == "folder")
        {
            Log.Info(Cat, $"release: INTO folder={intoFolder.Id} src={row.Kind}:{row.Id}");
            MoveRowIntoFolder(row, intoFolder.Id);
            return;
        }

        // Drop INTO the middle pane (cursor over the open folder's pane area
        // but not on a child row). Acts on the folder currently shown in the
        // middle pane.
        if (zone == DropZone.IntoMiddlePane)
        {
            var midFolderId = CurrentMiddleFolderId();
            if (midFolderId == null) { Log.Warn(Cat, "release: IntoMiddlePane but no current folder"); return; }
            Log.Info(Cat, $"release: INTO MIDDLE PANE folder={midFolderId} src={row.Kind}:{row.Id}");
            MoveRowIntoFolder(row, midFolderId);
            return;
        }

        // Drop ABOVE/BELOW in some list. If the target list differs from the
        // source, we're crossing buckets — do the row's Move in the target
        // collection so ApplyDropResultToVm can read its neighbours there.
        if (zone == DropZone.None || targetList == null)
        {
            Log.Info(Cat, "release: no zone — drop cancelled");
            return;
        }

        var rows = ReferenceEquals(targetList, LeftList) ? _leftRows : _middleRows;
        if (!ReferenceEquals(sourceList, targetList))
        {
            // Cross-list drop. The middle pane only ever represents the
            // current folder's children, so a cross-list above/below drop
            // here is effectively "move into the middle folder, at this slot".
            var midFolderId = CurrentMiddleFolderId();
            if (ReferenceEquals(targetList, MiddleList) && midFolderId != null)
            {
                Log.Info(Cat, $"release: cross-list to middle pane folder={midFolderId} src={row.Kind}:{row.Id} slot={slot}");
                MoveRowIntoFolder(row, midFolderId);
                return;
            }
            // Cross-list to left pane: fall through. We can't preview a
            // position via _middleRows for a row not in it, so just do an
            // approximate move-to-root.
            if (ReferenceEquals(targetList, LeftList))
            {
                Log.Info(Cat, $"release: cross-list to left pane src={row.Kind}:{row.Id}");
                if (row.Kind == "entry") _vm.MoveEntry(row.Id, null);
                else _vm.MoveFolder(row.Id, null);
                return;
            }
            return;
        }

        // Same-list above/below.
        int srcIdx = rows.IndexOf(row);
        if (srcIdx < 0) { Log.Warn(Cat, "release: row no longer in collection"); return; }

        int targetIdx;
        if (slot < 0) targetIdx = srcIdx;
        else if (slot > rows.Count) targetIdx = rows.Count - 1;
        else if (slot > srcIdx) targetIdx = slot - 1;
        else targetIdx = slot;
        if (targetIdx < 0) targetIdx = 0;
        if (targetIdx >= rows.Count) targetIdx = rows.Count - 1;
        if (targetIdx == srcIdx)
        {
            Log.Info(Cat, $"release: no-op (slot={slot} srcIdx={srcIdx})");
            return;
        }

        Log.Info(Cat, $"release: zone={zone} srcIdx={srcIdx} slot={slot} → targetIdx={targetIdx}");
        _dragKind = row.Kind; _dragId = row.Id; _dragBucket = row.Bucket;
        _suppressMovePersist = true;
        try { rows.Move(srcIdx, targetIdx); }
        finally { _suppressMovePersist = false; }
        try { ApplyDropResultToVm(rows); }
        catch (Exception ex) { Log.Error(Cat, "ApplyDropResultToVm threw", ex); }
        finally { _dragKind = null; _dragId = null; _dragBucket = null; }
    }

    /// <summary>Move the dragged row to be a child of the given folder.
    /// Validates nesting for folder-into-folder and auto-expands the target.</summary>
    private void MoveRowIntoFolder(AddRow row, string folderId)
    {
        if (row.Kind == "entry")
        {
            _vm.MoveEntry(row.Id, folderId);
        }
        else
        {
            if (!_vm.CanNest(row.Id, folderId))
            {
                Log.Warn(Cat, $"MoveRowIntoFolder: CanNest refused id={row.Id} parent={folderId}");
                return;
            }
            _vm.MoveFolder(row.Id, folderId);
        }
        _expanded.Add(folderId);
    }

    private void Page_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        Log.Info(Cat, $"Page_PointerCaptureLost active={_gripActive}");
        EndDragVisuals();
        _gripRow = null; _gripList = null; _gripActive = false;
        _gripTargetZone = DropZone.None; _gripTargetSlot = -1; _gripTargetRow = null;
    }

    // ---- Drag overlay helpers ------------------------------------------------

    private void BeginDragVisuals(AddRow row, ListView list)
    {
        // Populate the ghost label + icon.
        DragGhostLabel.Text = row.Label;
        if (IconLibrary.IsLibraryName(row.IconValue))
        {
            var p = IconRender.BuildIconElement(row.IconValue!, 16,
                (Brush)Application.Current.Resources["AppText"], thickness: 1.75);
            if (p != null) { DragGhostIconHost.Child = p; DragGhostIconHost.Visibility = Visibility.Visible; }
            else { DragGhostIconHost.Child = null; DragGhostIconHost.Visibility = Visibility.Collapsed; }
        }
        else { DragGhostIconHost.Child = null; DragGhostIconHost.Visibility = Visibility.Collapsed; }

        // Dim the source row container so it reads as "lifted".
        if (list.ContainerFromItem(row) is FrameworkElement src)
        {
            _gripSourceContainer = src;
            src.Opacity = 0.35;
        }
        DragGhost.Visibility = Visibility.Visible;
        DropIndicator.Visibility = Visibility.Collapsed;
    }

    private void EndDragVisuals()
    {
        DragGhost.Visibility = Visibility.Collapsed;
        DropIndicator.Visibility = Visibility.Collapsed;
        DropIntoBorder.Visibility = Visibility.Collapsed;
        if (_gripSourceContainer != null) { _gripSourceContainer.Opacity = 1.0; _gripSourceContainer = null; }
    }

    /// <summary>
    /// Compute the drop target under the cursor.
    /// • Folder rows have three zones — top 25% = Above (slot=i),
    ///   middle 50% = Into (move INTO this folder),
    ///   bottom 25% = Below (slot=i+1).
    /// • Entry rows are just split 50/50: Above (slot=i) or Below (slot=i+1).
    /// • Cursor above the first row → Above slot=0.
    /// • Cursor below the last row  → Below slot=Count.
    /// Self-drops are filtered (can't drop a row onto itself).
    /// </summary>
    private static (DropZone zone, int slot, AddRow? row) ComputeDropTarget(
        ListView list, System.Collections.Generic.IList<AddRow> rows, double y, AddRow draggedRow)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (list.ContainerFromIndex(i) is not FrameworkElement c) continue;
            var b = c.TransformToVisual(list).TransformBounds(new Windows.Foundation.Rect(0, 0, c.ActualWidth, c.ActualHeight));
            if (y < b.Top || y > b.Bottom) continue;

            var item = rows[i];
            // Skip self-drop entirely; let the cursor "fall through" to the
            // next row's hit-test slot.
            bool isSelf = item.Kind == draggedRow.Kind && item.Id == draggedRow.Id;

            double rel = (y - b.Top) / Math.Max(1, b.Height);
            if (item.Kind == "folder" && !isSelf)
            {
                if (rel < 0.25) return (DropZone.Above, i, null);
                if (rel > 0.75) return (DropZone.Below, i + 1, null);
                return (DropZone.Into, -1, item);
            }
            // Entry row (or the dragged folder itself — treat as a pure boundary).
            return rel < 0.5 ? (DropZone.Above, i, null) : (DropZone.Below, i + 1, null);
        }
        // Cursor not on any row → above/below the list.
        if (rows.Count == 0) return (DropZone.Above, 0, null);
        if (list.ContainerFromIndex(0) is FrameworkElement first)
        {
            var fb = first.TransformToVisual(list).TransformBounds(new Windows.Foundation.Rect(0, 0, first.ActualWidth, first.ActualHeight));
            if (y < fb.Top) return (DropZone.Above, 0, null);
        }
        return (DropZone.Below, rows.Count, null);
    }

    private void PositionDropTarget(ListView? list, DropZone zone, int slot, AddRow? intoRow)
    {
        // Reset every move; only the relevant indicator is enabled below.
        DropIndicator.Visibility = Visibility.Collapsed;
        DropIntoBorder.Visibility = Visibility.Collapsed;

        if (zone == DropZone.None || list == null) return;

        if (zone == DropZone.IntoMiddlePane)
        {
            // Highlight the entire middle pane.
            var mb = MiddlePane.TransformToVisual(DragOverlay).TransformBounds(
                new Windows.Foundation.Rect(0, 0, MiddlePane.ActualWidth, MiddlePane.ActualHeight));
            Canvas.SetLeft(DropIntoBorder, mb.Left + 1);
            Canvas.SetTop(DropIntoBorder, mb.Top + 1);
            DropIntoBorder.Width = mb.Width - 2;
            DropIntoBorder.Height = mb.Height - 2;
            DropIntoBorder.Visibility = Visibility.Visible;
            return;
        }

        if (zone == DropZone.Into && intoRow != null)
        {
            if (list.ContainerFromItem(intoRow) is FrameworkElement c)
            {
                var b = c.TransformToVisual(DragOverlay).TransformBounds(new Windows.Foundation.Rect(0, 0, c.ActualWidth, c.ActualHeight));
                Canvas.SetLeft(DropIntoBorder, b.Left + 2);
                Canvas.SetTop(DropIntoBorder, b.Top + 1);
                DropIntoBorder.Width = b.Width - 4;
                DropIntoBorder.Height = b.Height - 2;
                DropIntoBorder.Visibility = Visibility.Visible;
            }
            return;
        }

        // Above/Below: thin horizontal line at the slot boundary.
        var rows = ReferenceEquals(list, LeftList) ? _leftRows : _middleRows;
        FrameworkElement? anchor = null;
        bool below = false;
        if (slot >= rows.Count && rows.Count > 0)
        {
            anchor = list.ContainerFromIndex(rows.Count - 1) as FrameworkElement;
            below = true;
        }
        else if (slot >= 0 && slot < rows.Count)
        {
            anchor = list.ContainerFromIndex(slot) as FrameworkElement;
        }
        if (anchor == null) return;

        var ab = anchor.TransformToVisual(DragOverlay).TransformBounds(new Windows.Foundation.Rect(0, 0, anchor.ActualWidth, anchor.ActualHeight));
        double y = below ? ab.Bottom : ab.Top;
        Canvas.SetLeft(DropIndicator, ab.Left + 2);
        Canvas.SetTop(DropIndicator, y - 1);
        DropIndicator.Width = ab.Width - 4;
        DropIndicator.Visibility = Visibility.Visible;
    }

    private static ListView? FindAncestorListView(DependencyObject el)
    {
        var p = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(el);
        while (p != null)
        {
            if (p is ListView lv) return lv;
            p = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(p);
        }
        return null;
    }

    /// <summary>
    /// Persist the row's new bucket + position to the view-model after a
    /// successful manual drag. Reads the dragged row's neighbours in the
    /// post-drag <paramref name="rows"/> snapshot to infer the new bucket.
    /// The view-model mutation triggers <see cref="RefreshAll"/>, which
    /// rebuilds the row collections from canonical state — matches the
    /// visual order iff our inference is correct.
    /// </summary>
    private void ApplyDropResultToVm(System.Collections.Generic.IList<AddRow> rows)
    {
        if (_dragId == null || _dragKind == null) return;
        int newIdx = -1;
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Kind == _dragKind && rows[i].Id == _dragId) { newIdx = i; break; }
        if (newIdx < 0) { Log.Warn(Cat, "ApplyDropResultToVm: dragged row not found"); return; }

        AddRow? next = newIdx + 1 < rows.Count ? rows[newIdx + 1] : null;
        AddRow? prev = newIdx > 0 ? rows[newIdx - 1] : null;
        string? newBucketId;
        if (next != null) newBucketId = next.Bucket == "root" ? null : next.Bucket;
        else if (prev != null) newBucketId = prev.Bucket == "root" ? null : prev.Bucket;
        else newBucketId = null;

        Log.Info(Cat, $"ApplyDropResultToVm: newIdx={newIdx} oldBucket={_dragBucket} newBucket={newBucketId ?? "<root>"}");

        if (_dragKind == "entry")
        {
            _vm.MoveEntry(_dragId, newBucketId);
            _vm.ReorderEntryWithinBucket(_dragId,
                beforeEntryId: next?.Kind == "entry" && (next.Bucket == "root" ? null : next.Bucket) == newBucketId ? next.Id : null);
        }
        else
        {
            if (!_vm.MoveFolder(_dragId, newBucketId))
            {
                Log.Warn(Cat, $"MoveFolder refused — id={_dragId} newParent={newBucketId}");
                RefreshAll();
                return;
            }
            _vm.ReorderFolderWithinBucket(_dragId,
                beforeFolderId: next?.Kind == "folder" && (next.Bucket == "root" ? null : next.Bucket) == newBucketId ? next.Id : null);
        }
    }

    // -------------------------------------------------------------------------
    // ROW ADAPTER
    // -------------------------------------------------------------------------

}
