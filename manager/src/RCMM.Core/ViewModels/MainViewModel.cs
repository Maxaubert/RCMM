using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.Util;

namespace RCMM.Core.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly Scope[] AllScopes =
        { Scope.Files, Scope.Folders, Scope.Drives, Scope.Background, Scope.AllObjects, Scope.Folder };

    private readonly EntryScanner _scanner;
    private readonly HideService _hideService;
    private readonly Dictionary<Scope, ScopeListViewModel> _scopes;
    private readonly Dictionary<string, PendingChange> _pending = new();
    private bool _showBuiltIns = true;

    public ObservableCollection<PendingChange> PendingChanges { get; } = new();
    public ObservableCollection<EntryRowViewModel> AllEntries { get; } = new();

    public MainViewModel(EntryScanner scanner, HideService hideService)
    {
        _scanner = scanner;
        _hideService = hideService;
        _scopes = AllScopes.ToDictionary(s => s, s => new ScopeListViewModel(s));
    }

    public ScopeListViewModel GetScope(Scope scope) => _scopes[scope];

    public bool RequiresExplorerRestart
        => _pending.Values.Any(p => p.RequiresExplorerRestart);

    public bool ShowBuiltIns
    {
        get => _showBuiltIns;
        set
        {
            if (SetField(ref _showBuiltIns, value))
                RebuildAllEntries();
        }
    }

    public void Rescan()
    {
        foreach (var scope in AllScopes)
            _scopes[scope].Entries.Clear();

        foreach (var entry in _scanner.ScanAll())
        {
            var row = new EntryRowViewModel(entry);
            row.HiddenChanged = OnRowToggled;
            _scopes[entry.Scope].Entries.Add(row);
        }

        RebuildAllEntries();

        _pending.Clear();
        PendingChanges.Clear();
        Raise(nameof(RequiresExplorerRestart));
    }

    private void RebuildAllEntries()
    {
        AllEntries.Clear();
        var seen = new HashSet<string>();
        foreach (var scope in AllScopes)
        {
            foreach (var row in _scopes[scope].Entries)
            {
                if (!EntryFilters.IsLikelyUserVisible(row.Entry.DisplayName)) continue;
                if (row.Entry.IsBuiltIn && !_showBuiltIns) continue;
                var dedupeKey = $"{row.Entry.Kind}:{row.Entry.OriginalKeyName}";
                if (!seen.Add(dedupeKey)) continue;
                AllEntries.Add(row);
            }
        }
    }

    private void OnRowToggled(EntryRowViewModel row, bool isHidden)
    {
        var action = isHidden ? PendingAction.Hide : PendingAction.Unhide;
        // If the row's new state matches the underlying entry, drop the pending change.
        if (isHidden == row.Entry.IsHidden)
        {
            if (_pending.Remove(row.Entry.Id, out var stale))
                PendingChanges.Remove(stale);
        }
        else
        {
            var change = new PendingChange(row.Entry.Id, action,
                HideService.RequiresExplorerRestart(row.Entry.Kind));
            if (_pending.TryGetValue(row.Entry.Id, out var existing))
                PendingChanges.Remove(existing);
            _pending[row.Entry.Id] = change;
            PendingChanges.Add(change);
        }
        Raise(nameof(RequiresExplorerRestart));
    }

    public void ApplyPending()
    {
        foreach (var change in _pending.Values.ToList())
        {
            var entry = AllScopes
                .SelectMany(s => _scopes[s].Entries)
                .First(r => r.Entry.Id == change.EntryId)
                .Entry;

            if (change.Action == PendingAction.Hide) _hideService.Hide(entry);
            else _hideService.Unhide(entry);
        }
        _pending.Clear();
        PendingChanges.Clear();
        Raise(nameof(RequiresExplorerRestart));
    }
}
