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

    /// <summary>
    /// Flip an entry's hidden flag. Returns false when <paramref name="id"/> is not
    /// one of ours, which tells the caller to fall back to the normal registry-marker
    /// hide path. Returning true for an already-correct flag is deliberate: the entry
    /// is still ours, so the caller must not also write a registry marker.
    /// </summary>
    public bool SetEntryHidden(string id, bool hidden)
    {
        for (int i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].Id != id) continue;
            if (Entries[i].Hidden != hidden)
            {
                Entries[i] = Entries[i] with { Hidden = hidden };
                HasPendingChanges = true;
                Log.Debug(Cat, $"SetEntryHidden id={id} hidden={hidden}");
            }
            return true;
        }
        return false;
    }

    /// <summary>Persist through the store this view-model was built with. Callers
    /// must not reach for AdditionStore.DefaultPath() themselves — that ignores an
    /// injected store and writes the real user file from under a test.</summary>
    public void Save(AdditionState state) => _store.Save(state);

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
        SchemaVersion = AdditionState.CurrentSchemaVersion,
        Folders = Folders.ToList(),
        Entries = Entries.ToList(),
    };

    /// <summary>Called by the apply flow after AdditionStore.Save + AdditionApplier.Apply succeed.</summary>
    public void MarkClean() => HasPendingChanges = false;

    /// <summary>
    /// Maximum visible nesting depth of folders, per the design spec. The
    /// right-click chain is therefore at most RClick → A → B → C → entry.
    /// </summary>
    public const int MaxFolderDepth = 3;

    /// <summary>
    /// Compute a folder's depth in its current tree. Top-level folders are
    /// depth 1; depth grows as we walk up via ParentFolderId. Returns 1 if
    /// <paramref name="folderId"/> doesn't resolve.
    /// </summary>
    public int FolderDepth(string folderId)
    {
        int d = 1;
        var cur = Folders.FirstOrDefault(f => f.Id == folderId);
        while (cur?.ParentFolderId != null)
        {
            d++;
            if (d > 64) break; // belt + braces against pathological data
            cur = Folders.FirstOrDefault(f => f.Id == cur.ParentFolderId);
        }
        return d;
    }

    /// <summary>How deep the subtree below <paramref name="folderId"/> goes. 0 = no folder children.</summary>
    public int MaxSubtreeDepth(string folderId)
    {
        int max = 0;
        foreach (var child in Folders)
        {
            if (child.ParentFolderId != folderId) continue;
            var d = 1 + MaxSubtreeDepth(child.Id);
            if (d > max) max = d;
        }
        return max;
    }

    /// <summary>
    /// True iff moving <paramref name="movingId"/> under <paramref name="newParentId"/>
    /// would respect the 3-level depth cap and not create a cycle. Passing
    /// <c>null</c> for <paramref name="newParentId"/> (i.e. promote to top-level)
    /// is always allowed.
    /// </summary>
    public bool CanNest(string movingId, string? newParentId)
    {
        if (newParentId == null) return true;
        if (movingId == newParentId) return false;
        if (IsDescendant(maybeDescendant: newParentId, ancestorId: movingId)) return false;
        var parentDepth = FolderDepth(newParentId);
        var subtree = 1 + MaxSubtreeDepth(movingId);
        return parentDepth + subtree <= MaxFolderDepth;
    }

    private bool IsDescendant(string maybeDescendant, string ancestorId)
    {
        var cur = Folders.FirstOrDefault(f => f.Id == maybeDescendant);
        int hops = 0;
        while (cur != null)
        {
            if (cur.ParentFolderId == ancestorId) return true;
            cur = Folders.FirstOrDefault(f => f.Id == cur.ParentFolderId);
            if (++hops > 64) break;
        }
        return false;
    }

    /// <summary>
    /// Reparent an entry. <paramref name="newFolderId"/> = null promotes to top-level.
    /// No-op if the entry already has this parent. Also moves the entry to the END
    /// of its new bucket in <see cref="Entries"/> — drag-reorder calls
    /// <see cref="ReorderEntryWithinBucket"/> afterwards to position it precisely.
    /// </summary>
    public void MoveEntry(string id, string? newFolderId)
    {
        var idx = IndexOfEntry(id); if (idx < 0) return;
        var e = Entries[idx];
        if ((e.FolderId ?? null) == (newFolderId ?? null)) return;
        Entries.RemoveAt(idx);
        // Insert after the last existing entry in the same bucket to land at the end.
        var lastSiblingIdx = -1;
        for (int i = 0; i < Entries.Count; i++)
        {
            if ((Entries[i].FolderId ?? null) == (newFolderId ?? null)) lastSiblingIdx = i;
        }
        var updated = e with { FolderId = newFolderId };
        if (lastSiblingIdx < 0) Entries.Add(updated);
        else Entries.Insert(lastSiblingIdx + 1, updated);
        HasPendingChanges = true;
        Log.Debug(Cat, $"MoveEntry id={id} → folder={newFolderId ?? "<root>"}");
    }

    /// <summary>
    /// Reparent a folder. <paramref name="newParentId"/> = null promotes to top-level.
    /// Refuses (returns false) when the move would exceed the 3-level depth cap or
    /// create a cycle.
    /// </summary>
    public bool MoveFolder(string id, string? newParentId)
    {
        if (!CanNest(id, newParentId)) return false;
        var idx = IndexOfFolder(id); if (idx < 0) return false;
        var f = Folders[idx];
        if ((f.ParentFolderId ?? null) == (newParentId ?? null)) return true;
        Folders.RemoveAt(idx);
        var lastSiblingIdx = -1;
        for (int i = 0; i < Folders.Count; i++)
        {
            if ((Folders[i].ParentFolderId ?? null) == (newParentId ?? null)) lastSiblingIdx = i;
        }
        var updated = f with { ParentFolderId = newParentId };
        if (lastSiblingIdx < 0) Folders.Add(updated);
        else Folders.Insert(lastSiblingIdx + 1, updated);
        HasPendingChanges = true;
        Log.Debug(Cat, $"MoveFolder id={id} → parent={newParentId ?? "<root>"}");
        return true;
    }

    /// <summary>
    /// Move an entry within its current bucket so it lands immediately before
    /// <paramref name="beforeEntryId"/> (or at the end if null). The bucket is
    /// implicit — defined by the entry's current FolderId. No-op if the entry
    /// isn't in the same bucket as beforeEntryId.
    /// </summary>
    public void ReorderEntryWithinBucket(string id, string? beforeEntryId)
    {
        var idx = IndexOfEntry(id); if (idx < 0) return;
        var e = Entries[idx];
        Entries.RemoveAt(idx);
        if (beforeEntryId == null)
        {
            // Land at the END of e's bucket
            var lastSibling = -1;
            for (int i = 0; i < Entries.Count; i++)
                if ((Entries[i].FolderId ?? null) == (e.FolderId ?? null)) lastSibling = i;
            if (lastSibling < 0) Entries.Add(e);
            else Entries.Insert(lastSibling + 1, e);
        }
        else
        {
            var before = IndexOfEntry(beforeEntryId);
            if (before < 0) { Entries.Insert(idx, e); return; } // restore on failure
            Entries.Insert(before, e);
        }
        HasPendingChanges = true;
    }

    /// <summary>Same as <see cref="ReorderEntryWithinBucket"/> but for folders.</summary>
    public void ReorderFolderWithinBucket(string id, string? beforeFolderId)
    {
        var idx = IndexOfFolder(id); if (idx < 0) return;
        var f = Folders[idx];
        Folders.RemoveAt(idx);
        if (beforeFolderId == null)
        {
            var lastSibling = -1;
            for (int i = 0; i < Folders.Count; i++)
                if ((Folders[i].ParentFolderId ?? null) == (f.ParentFolderId ?? null)) lastSibling = i;
            if (lastSibling < 0) Folders.Add(f);
            else Folders.Insert(lastSibling + 1, f);
        }
        else
        {
            var before = IndexOfFolder(beforeFolderId);
            if (before < 0) { Folders.Insert(idx, f); return; }
            Folders.Insert(before, f);
        }
        HasPendingChanges = true;
    }

    private int IndexOfEntry(string id)
    {
        for (int i = 0; i < Entries.Count; i++) if (Entries[i].Id == id) return i;
        return -1;
    }
    private int IndexOfFolder(string id)
    {
        for (int i = 0; i < Folders.Count; i++) if (Folders[i].Id == id) return i;
        return -1;
    }
}
