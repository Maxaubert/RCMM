using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RCMM.Core.Models;
using RCMM.Core.ViewModels;

namespace RCMM.Views;

public sealed partial class AddPage : Page
{
    private NavArgs _args = null!;
    private AddPageViewModel _vm = null!;
    private ObservableCollection<Row> _rows = new();

    // Sentinel placeholder used in the Folder dropdown to mean "top-level".
    private const string TopLevelLabel = "(top-level)";

    public AddPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = (NavArgs)e.Parameter;
        _vm = _args.ViewModel.AddPage
              ?? throw new InvalidOperationException("AddPageViewModel not initialised on MainViewModel");

        // ComboBox sources are static; bind once.
        ScopeBox.ItemsSource = Enum.GetValues<AdditionScope>().Cast<object>().ToList();
        RunModeBox.ItemsSource = Enum.GetValues<RunMode>().Cast<object>().ToList();

        RebuildRows();

        // Refresh the list whenever the view-model changes (template add, folder
        // delete, etc.). We rebuild fully because the displayed order depends on
        // folder membership, so an in-place edit isn't trivial.
        _vm.Entries.CollectionChanged += (_, __) => RebuildRows();
        _vm.Folders.CollectionChanged += (_, __) => RebuildRows();
    }

    private void RebuildRows()
    {
        _rows.Clear();
        // Folder rows + their children
        foreach (var folder in _vm.Folders)
        {
            _rows.Add(new Row { Kind = RowKind.Folder, Folder = folder });
            foreach (var entry in _vm.Entries.Where(e => e.FolderId == folder.Id))
                _rows.Add(new Row { Kind = RowKind.Entry, Entry = entry, IsChild = true });
        }
        // Top-level entries (no folder)
        foreach (var entry in _vm.Entries.Where(e => string.IsNullOrEmpty(e.FolderId)))
            _rows.Add(new Row { Kind = RowKind.Entry, Entry = entry });

        ItemsList.ItemsSource = _rows;
    }

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
        SelectAndEdit(entry.Id);
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = new AdditionFolder { Id = Guid.NewGuid().ToString("N"), Name = "New folder" };
        _vm.AddFolder(folder);
    }

    private void Templates_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(TemplatesPage), _args);
    }

    private void SelectAndEdit(string entryId)
    {
        var row = _rows.FirstOrDefault(r => r.Kind == RowKind.Entry && r.Entry!.Id == entryId);
        if (row == null) return;
        ItemsList.SelectedItem = row;
    }

    private AdditionEntry? _selectedEntry;
    private AdditionFolder? _selectedFolder;

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedItem is Row row)
        {
            if (row.Kind == RowKind.Entry && row.Entry != null) ShowEntryEditor(row.Entry);
            else if (row.Kind == RowKind.Folder && row.Folder != null) ShowFolderEditor(row.Folder);
            else { EditorPanel.Visibility = Visibility.Collapsed; }
        }
        else { EditorPanel.Visibility = Visibility.Collapsed; _selectedEntry = null; _selectedFolder = null; }
    }

    private bool _suppressFieldChange;

    private void ShowEntryEditor(AdditionEntry entry)
    {
        _selectedEntry = entry;
        _selectedFolder = null;
        EditorPanel.Visibility = Visibility.Visible;
        EditorTitle.Text = "Edit entry";
        SetEntryFieldsVisibility(true);

        _suppressFieldChange = true;
        try
        {
            NameBox.Text = entry.Name;
            CommandBox.Text = entry.Command;
            WorkingDirBox.Text = entry.WorkingDir;
            IconBox.Text = entry.Icon ?? "";
            FileTypesBox.Text = entry.FileTypes is { Count: > 0 } ? string.Join(", ", entry.FileTypes) : "";
            ScopeBox.SelectedItem = entry.Scope;
            RunModeBox.SelectedItem = entry.RunMode;

            var folderOptions = new List<object> { TopLevelLabel };
            foreach (var f in _vm.Folders) folderOptions.Add(f);
            FolderBox.ItemsSource = folderOptions;
            FolderBox.DisplayMemberPath = "";
            FolderBox.SelectedItem = entry.FolderId == null
                ? (object)TopLevelLabel
                : _vm.Folders.FirstOrDefault(f => f.Id == entry.FolderId) ?? (object)TopLevelLabel;
        }
        finally { _suppressFieldChange = false; }
    }

    private void ShowFolderEditor(AdditionFolder folder)
    {
        _selectedFolder = folder;
        _selectedEntry = null;
        EditorPanel.Visibility = Visibility.Visible;
        EditorTitle.Text = "Edit folder";
        SetEntryFieldsVisibility(false);

        _suppressFieldChange = true;
        try
        {
            NameBox.Text = folder.Name;
            IconBox.Text = folder.Icon ?? "";
        }
        finally { _suppressFieldChange = false; }
    }

    private void SetEntryFieldsVisibility(bool showEntryFields)
    {
        var vis = showEntryFields ? Visibility.Visible : Visibility.Collapsed;
        CommandLabel.Visibility = vis; CommandBox.Visibility = vis;
        WorkingDirLabel.Visibility = vis; WorkingDirBox.Visibility = vis;
        ScopeLabel.Visibility = vis; ScopeBox.Visibility = vis;
        FileTypesLabel.Visibility = vis; FileTypesBox.Visibility = vis;
        FolderLabel.Visibility = vis; FolderBox.Visibility = vis;
        RunModeLabel.Visibility = vis; RunModeBox.Visibility = vis;
    }

    private void Field_Changed(object sender, RoutedEventArgs e) => SaveCurrent();
    private void Field_SelectionChanged(object sender, SelectionChangedEventArgs e) => SaveCurrent();

    private void SaveCurrent()
    {
        if (_suppressFieldChange) return;
        if (_selectedEntry != null)
        {
            var folderId = FolderBox.SelectedItem is AdditionFolder f ? f.Id : null;
            var updated = _selectedEntry with
            {
                Name = NameBox.Text,
                Command = CommandBox.Text,
                WorkingDir = WorkingDirBox.Text,
                Icon = string.IsNullOrWhiteSpace(IconBox.Text) ? null : IconBox.Text,
                Scope = ScopeBox.SelectedItem is AdditionScope s ? s : AdditionScope.FolderBackground,
                RunMode = RunModeBox.SelectedItem is RunMode r ? r : RunMode.VisibleTerminal,
                FileTypes = string.IsNullOrWhiteSpace(FileTypesBox.Text)
                    ? null
                    : FileTypesBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                FolderId = folderId,
            };
            _vm.ReplaceEntry(updated);
            _selectedEntry = updated;
        }
        else if (_selectedFolder != null)
        {
            var updated = _selectedFolder with
            {
                Name = NameBox.Text,
                Icon = string.IsNullOrWhiteSpace(IconBox.Text) ? null : IconBox.Text,
            };
            _vm.ReplaceFolder(updated);
            _selectedFolder = updated;
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry != null)
        {
            _vm.DeleteEntry(_selectedEntry.Id);
            _selectedEntry = null;
            EditorPanel.Visibility = Visibility.Collapsed;
        }
        else if (_selectedFolder != null)
        {
            _vm.DeleteFolder(_selectedFolder.Id);
            _selectedFolder = null;
            EditorPanel.Visibility = Visibility.Collapsed;
        }
    }

    // Adapter row for the list. Holds either an Entry or a Folder, and exposes
    // pre-computed display strings so the DataTemplate stays declarative.
    public sealed class Row
    {
        public RowKind Kind { get; set; }
        public AdditionEntry? Entry { get; set; }
        public AdditionFolder? Folder { get; set; }
        public bool IsChild { get; set; }
        public string Display => Kind == RowKind.Folder
            ? "▸ " + (Folder?.Name ?? "")
            : (IsChild ? "   " : "") + (Entry?.Name ?? "");
        public string GlyphForKind => Kind == RowKind.Folder ? "" : "";
    }

    public enum RowKind { Folder, Entry }
}
