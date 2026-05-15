using System.Collections.ObjectModel;
using System.Linq;
using RCMM.Core.Diagnostics;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Core.ViewModels;

/// <summary>
/// In-memory editor state for the Add page. Buffers user edits as pending
/// changes until the footer Apply button commits them via AdditionApplier
/// and AdditionStore.Save. Doesn't talk to the registry directly.
/// </summary>
public sealed class AddPageViewModel : ObservableObject
{
    private const string Cat = "addvm";
    private readonly AdditionStore _store;

    public ObservableCollection<AdditionEntry> Entries { get; } = new();
    public ObservableCollection<AdditionFolder> Folders { get; } = new();

    private bool _hasPendingChanges;
    public bool HasPendingChanges
    {
        get => _hasPendingChanges;
        private set => SetField(ref _hasPendingChanges, value);
    }

    public AddPageViewModel(AdditionStore store) { _store = store; }

    public void Load()
    {
        var state = _store.Load();
        Entries.Clear();
        Folders.Clear();
        foreach (var e in state.Entries) Entries.Add(e);
        foreach (var f in state.Folders) Folders.Add(f);
        HasPendingChanges = false;
    }

    public void AddEntry(AdditionEntry entry)
    {
        Entries.Add(entry);
        HasPendingChanges = true;
        Log.Debug(Cat, $"AddEntry id={entry.Id} name='{entry.Name}'");
    }

    public void DeleteEntry(string id)
    {
        var existing = Entries.FirstOrDefault(e => e.Id == id);
        if (existing == null) return;
        Entries.Remove(existing);
        HasPendingChanges = true;
        Log.Debug(Cat, $"DeleteEntry id={id}");
    }

    public void ReplaceEntry(AdditionEntry replacement)
    {
        for (int i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].Id != replacement.Id) continue;
            Entries[i] = replacement;
            HasPendingChanges = true;
            Log.Debug(Cat, $"ReplaceEntry id={replacement.Id} name='{replacement.Name}'");
            return;
        }
    }

    public void AddFolder(AdditionFolder folder)
    {
        Folders.Add(folder);
        HasPendingChanges = true;
        Log.Debug(Cat, $"AddFolder id={folder.Id} name='{folder.Name}'");
    }

    public void DeleteFolder(string id)
    {
        var folder = Folders.FirstOrDefault(f => f.Id == id);
        if (folder == null) return;
        Folders.Remove(folder);
        // Move child entries to top-level so they aren't silently lost.
        for (int i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].FolderId == id)
                Entries[i] = Entries[i] with { FolderId = null };
        }
        HasPendingChanges = true;
        Log.Debug(Cat, $"DeleteFolder id={id}");
    }

    public void ReplaceFolder(AdditionFolder replacement)
    {
        for (int i = 0; i < Folders.Count; i++)
        {
            if (Folders[i].Id != replacement.Id) continue;
            Folders[i] = replacement;
            HasPendingChanges = true;
            return;
        }
    }

    public AdditionState Snapshot() => new()
    {
        SchemaVersion = 1,
        Folders = Folders.ToList(),
        Entries = Entries.ToList(),
    };

    /// <summary>Called by the apply flow after AdditionStore.Save + AdditionApplier.Apply succeed.</summary>
    public void MarkClean() => HasPendingChanges = false;
}
