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

    // Drag-state — captured on DragItemsStarting; consumed in DragOver/Drop.
    private string? _dragKind;
    private string? _dragId;
    private string? _dragBucket;

    public AddPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = (NavArgs)e.Parameter;
        _vm = _args.ViewModel.AddPage
              ?? throw new InvalidOperationException("AddPageViewModel not initialised on MainViewModel");

        // Static combo sources
        ScopeBox.ItemsSource    = Enum.GetValues<AdditionScope>().Cast<object>().ToList();
        RunModeBox.ItemsSource  = Enum.GetValues<RunMode>().Cast<object>().ToList();

        LeftList.ItemsSource = _leftRows;
        MiddleList.ItemsSource = _middleRows;

        RebuildLeftRows();
        RebuildMiddleRows();
        RenderEditor();

        // Refresh on every VM mutation. Apply/Discard land here too.
        _vm.Entries.CollectionChanged += (_, __) => RefreshAll();
        _vm.Folders.CollectionChanged += (_, __) => RefreshAll();
    }

    private void RefreshAll()
    {
        RebuildLeftRows();
        RebuildMiddleRows();
        RenderEditor();
    }

    // -------------------------------------------------------------------------
    // ROW REBUILD — left + middle
    // -------------------------------------------------------------------------

    private void RebuildLeftRows()
    {
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
            LeftCol.Width = new GridLength(340);
            return;
        }
        MiddlePane.Visibility = Visibility.Visible;
        MiddleCol.Width = new GridLength(1, GridUnitType.Star);
        LeftCol.Width = new GridLength(300);

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

    /// <summary>Fires when a left-pane row's content Grid is realised. Renders
    /// indent margin, selection bar, and the library icon (if any).</summary>
    private void RowRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid g || g.DataContext is not AddRow r) return;
        ApplyLeftRowVisuals(g, r);
    }

    private void ApplyLeftRowVisuals(Grid g, AddRow r)
    {
        // Indent (column 0)
        if (g.FindName("IndentSpacer") is Border indent)
            indent.Width = r.Indent * 22;
        // Selection bar
        if (g.FindName("SelBar") is Microsoft.UI.Xaml.Shapes.Rectangle selBar)
            selBar.Visibility = (r.Kind == _selectedKind && r.Id == _selectedId) ? Visibility.Visible : Visibility.Collapsed;
        // Icon
        if (g.FindName("IconPath") is Microsoft.UI.Xaml.Shapes.Path iconPath)
        {
            if (IconLibrary.IsLibraryName(r.IconValue))
            {
                var name = IconLibrary.StripPrefix(r.IconValue)!;
                var geom = IconRender.GetGeometry(name);
                if (geom != null)
                {
                    iconPath.Data = geom;
                    iconPath.Visibility = Visibility.Visible;
                }
                else iconPath.Visibility = Visibility.Collapsed;
            }
            else iconPath.Visibility = Visibility.Collapsed;
        }
    }

    private void MidRowRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid g || g.DataContext is not AddRow r) return;
        if (g.FindName("MidIconPath") is Microsoft.UI.Xaml.Shapes.Path iconPath)
        {
            if (IconLibrary.IsLibraryName(r.IconValue))
            {
                var name = IconLibrary.StripPrefix(r.IconValue)!;
                var geom = IconRender.GetGeometry(name);
                if (geom != null) { iconPath.Data = geom; iconPath.Visibility = Visibility.Visible; }
                else iconPath.Visibility = Visibility.Collapsed;
            }
            else iconPath.Visibility = Visibility.Collapsed;
        }
    }

    // -------------------------------------------------------------------------
    // SELECTION + EXPAND
    // -------------------------------------------------------------------------

    private void LeftList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LeftList.SelectedItem is not AddRow row) return;
        _selectedKind = row.Kind;
        _selectedId = row.Id;
        _middlePath.Clear();
        RebuildMiddleRows();
        RenderEditor();
        // Repaint selection bars on currently realised rows
        RepaintSelectionBars();
    }

    private void RepaintSelectionBars()
    {
        foreach (var item in _leftRows)
        {
            if (LeftList.ContainerFromItem(item) is ListViewItem li
                && li.ContentTemplateRoot is Grid g
                && g.FindName("SelBar") is Microsoft.UI.Xaml.Shapes.Rectangle bar)
            {
                bar.Visibility = (item.Kind == _selectedKind && item.Id == _selectedId)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
        }
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
            RenderEditor();
            RepaintSelectionBars();
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
    }

    private void RenderIconPicker(string? iconValue)
    {
        // Clear preview
        IconPreviewCell.Children.Clear();
        IconCustomBox.Text = "";
        if (IconLibrary.IsLibraryName(iconValue))
        {
            var name = IconLibrary.StripPrefix(iconValue)!;
            var geom = IconRender.GetGeometry(name);
            if (geom != null)
            {
                var p = new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = geom,
                    Width = 24, Height = 24,
                    Stretch = Stretch.Uniform,
                    Stroke = (Brush)Application.Current.Resources["AppText"],
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Fill = null,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                IconPreviewCell.Children.Add(p);
            }
            IconLabelText.Text = name;
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
    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Live-update label as the user types
        if (_suppressFieldChange) return;
        if (_selectedKind == "entry" && _vm.Entries.FirstOrDefault(x => x.Id == _selectedId) is { } entry)
            _vm.ReplaceEntry(entry with { Name = NameBox.Text });
        else if ((_selectedKind == "folder" || CurrentMiddleFolderId() != null))
        {
            var fid = CurrentMiddleFolderId() ?? _selectedId;
            if (fid != null && _vm.Folders.FirstOrDefault(f => f.Id == fid) is { } folder)
                _vm.ReplaceFolder(folder with { Name = NameBox.Text });
        }
    }

    private void SaveCurrent()
    {
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
            };
            _vm.ReplaceEntry(updated);
            if ((entry.FolderId ?? null) != (newFolderId ?? null))
                _vm.MoveEntry(entry.Id, newFolderId); // also reflows order
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
                _vm.ReplaceFolder(updated);
                if ((folder.ParentFolderId ?? null) != (newParent ?? null))
                {
                    if (!_vm.MoveFolder(folder.Id, newParent))
                    {
                        Log.Warn(Cat, $"folder move refused: depth cap or cycle (id={folder.Id} new parent={newParent})");
                        // Revert UI selection back to current
                        ParentFolderBox.SelectedItem = folder.ParentFolderId == null
                            ? (object)TopLevelLabel
                            : _vm.Folders.FirstOrDefault(x => x.Id == folder.ParentFolderId) ?? (object)TopLevelLabel;
                    }
                }
            }
        }
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
        var current = CurrentIconValue();
        var dialog = new IconPickerDialog(current) { XamlRoot = this.XamlRoot };
        await dialog.ShowAsync();
        if (dialog.PickedValue != null && dialog.PickedValue != current)
            SetCurrentIcon(dialog.PickedValue);
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
    // DRAG & DROP — left list
    // -------------------------------------------------------------------------

    private void LeftList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.Count == 0) { e.Cancel = true; return; }
        if (e.Items[0] is not AddRow row) { e.Cancel = true; return; }
        _dragKind = row.Kind; _dragId = row.Id; _dragBucket = row.Bucket;
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText("rcmm:" + row.Kind + ":" + row.Id);
    }

    private void LeftList_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Move;
        ClearAllDropMarks();
        // Find which row container is under the pointer
        var (row, container, zone) = HitTestRow(LeftList, e, isLeft: true);
        if (row == null || container == null) return;
        if (row.Kind == _dragKind && row.Id == _dragId) return;

        // Validate folder-nesting depth on into/cross-bucket
        if (_dragKind == "folder")
        {
            if (zone == "into")
            {
                if (!_vm.CanNest(_dragId!, row.Id))
                { e.AcceptedOperation = DataPackageOperation.None; return; }
            }
            else
            {
                // above/below — target bucket must accept this folder
                var targetBucket = row.Bucket == "root" ? null : row.Bucket;
                if (targetBucket != null && !_vm.CanNest(_dragId!, targetBucket))
                { e.AcceptedOperation = DataPackageOperation.None; return; }
            }
        }

        ShowDropMark(container, zone, isLeft: true);
        e.DragUIOverride.Caption = zone == "into"
            ? "Move into " + row.Label
            : (zone == "above" ? "Move above " + row.Label : "Move below " + row.Label);
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = false;
    }

    private void LeftList_Drop(object sender, DragEventArgs e)
    {
        ClearAllDropMarks();
        var (row, _, zone) = HitTestRow(LeftList, e, isLeft: true);
        if (row == null || _dragKind == null || _dragId == null) return;
        if (row.Kind == _dragKind && row.Id == _dragId) return;

        try
        {
            if (zone == "into" && row.Kind == "folder")
            {
                MoveDragToBucket(row.Id);
            }
            else
            {
                // above/below in target's bucket
                var targetBucket = row.Bucket == "root" ? null : row.Bucket;
                if (_dragBucket != row.Bucket)
                    MoveDragToBucket(targetBucket);
                // Now reorder within that bucket so dragged lands adjacent to row.
                if (_dragKind == "entry") _vm.ReorderEntryWithinBucket(_dragId, beforeEntryId: zone == "above" ? row.Id : NextSiblingEntryId(row));
                else _vm.ReorderFolderWithinBucket(_dragId, beforeFolderId: zone == "above" ? row.Id : NextSiblingFolderId(row));
            }
        }
        finally
        {
            _dragKind = null; _dragId = null; _dragBucket = null;
        }
    }

    private string? NextSiblingEntryId(AddRow row)
    {
        // Used when dropping BELOW `row` — we need to move before the NEXT entry
        // in the same bucket, or to the end (null) if there isn't one.
        var bucketId = row.Bucket == "root" ? null : row.Bucket;
        var siblings = _vm.Entries.Where(e => (e.FolderId ?? null) == bucketId).ToList();
        var idx = siblings.FindIndex(e => e.Id == row.Id);
        if (idx < 0 || idx + 1 >= siblings.Count) return null;
        return siblings[idx + 1].Id;
    }
    private string? NextSiblingFolderId(AddRow row)
    {
        var bucketId = row.Bucket == "root" ? null : row.Bucket;
        var siblings = _vm.Folders.Where(f => (f.ParentFolderId ?? null) == bucketId).ToList();
        var idx = siblings.FindIndex(f => f.Id == row.Id);
        if (idx < 0 || idx + 1 >= siblings.Count) return null;
        return siblings[idx + 1].Id;
    }

    private void MoveDragToBucket(string? newBucketFolderId)
    {
        if (_dragKind == "entry")
            _vm.MoveEntry(_dragId!, newBucketFolderId);
        else
            _vm.MoveFolder(_dragId!, newBucketFolderId);
        _dragBucket = newBucketFolderId ?? "root";
        // Auto-expand the target folder so the user sees the move land
        if (newBucketFolderId != null) _expanded.Add(newBucketFolderId);
    }

    // -------------------------------------------------------------------------
    // DRAG & DROP — middle list
    // -------------------------------------------------------------------------

    private void MiddleList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.Count == 0) { e.Cancel = true; return; }
        if (e.Items[0] is not AddRow row) { e.Cancel = true; return; }
        _dragKind = row.Kind; _dragId = row.Id; _dragBucket = row.Bucket;
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText("rcmm:" + row.Kind + ":" + row.Id);
    }

    private void MiddleList_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Move;
        ClearAllDropMarks();
        var (row, container, zone) = HitTestRow(MiddleList, e, isLeft: false);
        if (row != null && container != null)
        {
            if (row.Kind == _dragKind && row.Id == _dragId) return;
            ShowDropMark(container, zone, isLeft: false);
            return;
        }
        // Empty area in middle pane → drop-into current folder
        var folderId = CurrentMiddleFolderId();
        if (folderId == null) return;
        if (_dragBucket == folderId) return; // already in here
        if (_dragKind == "folder" && !_vm.CanNest(_dragId!, folderId))
        { e.AcceptedOperation = DataPackageOperation.None; return; }
        // Visual: tint the whole pane briefly via the count text. (Simple hint.)
        MiddleCount.Foreground = (Brush)Application.Current.Resources["AppAccent"];
    }

    private void MiddleList_Drop(object sender, DragEventArgs e)
    {
        MiddleCount.Foreground = (Brush)Application.Current.Resources["AppTextMuted"];
        ClearAllDropMarks();
        var (row, _, zone) = HitTestRow(MiddleList, e, isLeft: false);
        var folderId = CurrentMiddleFolderId();
        if (folderId == null || _dragKind == null || _dragId == null) return;
        try
        {
            if (row != null && row.Kind != null && (row.Kind != _dragKind || row.Id != _dragId))
            {
                if (zone == "into" && row.Kind == "folder")
                    MoveDragToBucket(row.Id);
                else
                {
                    if (_dragBucket != folderId) MoveDragToBucket(folderId);
                    if (_dragKind == "entry") _vm.ReorderEntryWithinBucket(_dragId, beforeEntryId: zone == "above" ? row.Id : NextSiblingEntryId(row));
                    else _vm.ReorderFolderWithinBucket(_dragId, beforeFolderId: zone == "above" ? row.Id : NextSiblingFolderId(row));
                }
            }
            else
            {
                // Drop in the empty area → add to current folder at the end
                if (_dragKind == "folder" && !_vm.CanNest(_dragId, folderId)) return;
                MoveDragToBucket(folderId);
            }
        }
        finally { _dragKind = null; _dragId = null; _dragBucket = null; }
    }

    // -------------------------------------------------------------------------
    // HIT TEST + DROP MARKS
    // -------------------------------------------------------------------------

    /// <summary>Find which row's container the pointer is over and classify the
    /// drop into above/below/into. Returns nulls for the row when no row is hit.</summary>
    private (AddRow? row, FrameworkElement? container, string zone) HitTestRow(ListView list, DragEventArgs e, bool isLeft)
    {
        var pos = e.GetPosition(list);
        var listSource = isLeft ? _leftRows : _middleRows;
        foreach (var item in listSource)
        {
            if (list.ContainerFromItem(item) is not ListViewItem li) continue;
            var bounds = li.TransformToVisual(list).TransformBounds(new Windows.Foundation.Rect(0, 0, li.ActualWidth, li.ActualHeight));
            if (pos.Y < bounds.Top || pos.Y > bounds.Bottom) continue;
            var rel = (pos.Y - bounds.Top) / Math.Max(1, bounds.Height);
            // Folders get a middle "drop into" zone occupying 50% of the row.
            // Entries split top/bottom 50/50.
            bool isFolder = item.Kind == "folder";
            string zone;
            if (isFolder)
            {
                if (rel < 0.25) zone = "above";
                else if (rel > 0.75) zone = "below";
                else zone = "into";
            }
            else
            {
                zone = rel < 0.5 ? "above" : "below";
            }
            return (item, li, zone);
        }
        return (null, null, "");
    }

    private void ShowDropMark(FrameworkElement container, string zone, bool isLeft)
    {
        if (container is not ListViewItem li || li.ContentTemplateRoot is not Grid g) return;
        // Names differ between left and middle templates — try both.
        var topName    = isLeft ? "DropTop"    : "MidDropTop";
        var bottomName = isLeft ? "DropBottom" : "MidDropBottom";
        var intoName   = isLeft ? "DropIntoFill" : "MidDropIntoFill";
        if (g.FindName(topName)    is Microsoft.UI.Xaml.Shapes.Rectangle t) t.Visibility = zone == "above" ? Visibility.Visible : Visibility.Collapsed;
        if (g.FindName(bottomName) is Microsoft.UI.Xaml.Shapes.Rectangle b) b.Visibility = zone == "below" ? Visibility.Visible : Visibility.Collapsed;
        if (g.FindName(intoName)   is Microsoft.UI.Xaml.Shapes.Rectangle into) into.Visibility = zone == "into" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearAllDropMarks()
    {
        foreach (var src in new[] { (_leftRows, LeftList, true), (_middleRows, MiddleList, false) })
        {
            foreach (var item in src.Item1)
            {
                if (src.Item2.ContainerFromItem(item) is not ListViewItem li) continue;
                if (li.ContentTemplateRoot is not Grid g) continue;
                foreach (var n in new[] { "DropTop","DropBottom","DropIntoFill","MidDropTop","MidDropBottom","MidDropIntoFill" })
                {
                    if (g.FindName(n) is Microsoft.UI.Xaml.Shapes.Rectangle r) r.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // ROW ADAPTER
    // -------------------------------------------------------------------------

    public sealed class AddRow
    {
        public required string Kind { get; init; }     // "folder" | "entry"
        public required string Id   { get; init; }
        public required string Bucket { get; init; }   // "root" | parent folder id
        public int Indent { get; init; }
        public required string Label { get; init; }
        public string? Badge { get; init; }
        public string? IconValue { get; init; }
        public bool IsExpanded { get; init; }
        public string? SubText { get; init; }           // middle pane only
        public string? Ordinal { get; init; }           // middle pane only

        public string TwistGlyph => Kind == "folder" ? (IsExpanded ? "▾" : "▸") : "";
        public Visibility BadgeVisibility => string.IsNullOrEmpty(Badge) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility SubVisibility => string.IsNullOrEmpty(SubText) ? Visibility.Collapsed : Visibility.Visible;
    }
}
