using System;
using System.Collections.Generic;
using System.Linq;
using RCMM.Core.Diagnostics;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

/// <summary>
/// Writes user-defined add-to-menu entries and folders to HKCU as classic
/// shell verbs. Every key uses the RCMM. prefix so a wholesale tear-down +
/// re-write makes Apply idempotent.
///
/// Schema v2 brings two changes:
///   1. Folders may nest (AdditionFolder.ParentFolderId). The applier walks
///      the folder tree recursively, writing each nested folder verb under
///      its parent's <c>ContextMenus</c> subtree with its own
///      <c>ExtendedSubCommandsKey</c> pointing further down.
///   2. Verb key names are prefixed with a 3-digit ordinal reflecting the
///      item's position within its bucket — <c>RCMM.&lt;ord&gt;.&lt;id&gt;</c>.
///      Windows orders classic verbs alphabetically by key name, so the
///      ordinal prefix is what makes drag-to-reorder produce the user's
///      chosen order in the actual right-click menu.
/// </summary>
public sealed class AdditionApplier
{
    private const string Cat = "addapply";
    private const string VerbPrefix = "RCMM.";
    private const string ClassesRoot = "Software\\Classes";
    private static readonly string[] _staticScopeRoots =
    {
        "Directory\\Background",
        "Directory",
        "Drive",
        "AllFilesystemObjects",
        "*",
    };

    private readonly IRegistry _reg;
    private readonly IconMaterializer? _icons;

    public AdditionApplier(IRegistry reg, IconMaterializer? icons = null)
    {
        _reg = reg;
        _icons = icons;
    }

    /// <summary>Resolve an Icon value for the registry: library refs
    /// (<c>lib:&lt;name&gt;</c>) get materialized to an .ico file path; raw
    /// values pass through. Null materialization (unknown name / failure)
    /// falls back to skipping the Icon write so the shell doesn't try to
    /// resolve an unresolvable string.</summary>
    private string? ResolveIcon(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (_icons == null) return IconLibrary.IsLibraryName(raw) ? null : raw;
        return _icons.Materialize(raw);
    }

    public static string ScopeRootFor(AdditionScope scope) => scope switch
    {
        AdditionScope.FolderBackground       => "Directory\\Background",
        AdditionScope.Folder                 => "Directory",
        AdditionScope.Drive                  => "Drive",
        AdditionScope.AllFilesystemObjects   => "AllFilesystemObjects",
        AdditionScope.File                   => "*",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
    };

    /// <summary>Wrap the bare command for execution, honoring the entry's chosen
    /// terminal. Default (null terminal) reproduces the historical behavior:
    /// VisibleTerminal → "cmd /k …", Background → command as-is.</summary>
    public static string WrapForRunMode(RunMode mode, string command, string? terminal = null)
        => TerminalCatalog.Wrap(command, mode, terminal);

    public static IReadOnlyList<string> EntryScopePaths(AdditionEntry entry)
    {
        if (entry.Scope != AdditionScope.File)
            return new[] { ScopeRootFor(entry.Scope) };
        if (entry.FileTypes == null || entry.FileTypes.Count == 0)
            return new[] { "*" };
        return entry.FileTypes.Select(ext => ext.StartsWith('.') ? ext : "." + ext).ToList();
    }

    /// <summary>
    /// 3-digit ordinal prefix that forces Windows' alphabetical verb-key order
    /// to match the user's chosen order. Verb names become
    /// <c>RCMM.&lt;ord&gt;.&lt;id&gt;</c>. The dot between the parts is kept so the
    /// purge logic (which matches "RCMM." as a prefix) still owns these keys
    /// cleanly. Ordinals start at 1 and pad to 3 digits.
    /// </summary>
    internal static string VerbName(int ordinal, string id)
        => $"{VerbPrefix}{ordinal:D3}.{id}";

    public void Apply(AdditionState state)
    {
        Log.Info(Cat, $"Apply begin entries={state.Entries.Count} folders={state.Folders.Count}");

        // Collect all File-scope extensions used anywhere in the state so purge
        // also cleans those scope-roots. Without this a previously-applied .png
        // entry would survive a state that no longer references .png.
        var extraExts = state.Entries
            .Where(e => e.Scope == AdditionScope.File && e.FileTypes is { Count: > 0 })
            .SelectMany(e => e.FileTypes!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        PurgeOwnedKeys(extraExts);

        var folderById = state.Folders.ToDictionary(f => f.Id, f => f);
        var foldersByParent = BuildFolderByParent(state.Folders);
        var entriesByFolder = BuildEntriesByFolder(state.Entries);

        // For each scope-root the state uses, walk the top-level forest and recurse.
        // The top-level bucket has key "" (empty string in our parent-id-keyed maps).
        var scopeRoots = CollectScopeRoots(state, folderById);
        foreach (var scope in scopeRoots)
        {
            var topFolders = foldersByParent.TryGetValue("", out var tfs) ? (IReadOnlyList<AdditionFolder>)tfs : Array.Empty<AdditionFolder>();
            var topEntries = entriesByFolder.TryGetValue("", out var tes) ? (IReadOnlyList<AdditionEntry>)tes : Array.Empty<AdditionEntry>();
            WriteBucketInternal(scope, parentContainer: null, topFolders, topEntries, state, folderById, foldersByParent, entriesByFolder);
        }

        // Orphan entries (FolderId set but folder missing) — drop them at top-level.
        var orphans = state.Entries
            .Where(e => !string.IsNullOrEmpty(e.FolderId) && !folderById.ContainsKey(e.FolderId!))
            .ToList();
        if (orphans.Count > 0)
        {
            // Synthesize a fake bucket and write orphans inline as top-level entries.
            int ord = 0;
            foreach (var orphan in orphans)
            {
                ord++;
                Log.Warn(Cat, $"entry {orphan.Name} references missing folder {orphan.FolderId} — writing as top-level");
                foreach (var scopePrefix in EntryScopePaths(orphan))
                    WriteEntry(orphan, ord, scopePrefix, parentContainer: null);
            }
        }

        Log.Info(Cat, "Apply end");
    }

    /// <summary>
    /// Recursive worker: for a single <paramref name="scope"/> and a single bucket
    /// path (top-level scope, or a parent folder's ContextMenus path), write each
    /// item that participates in this scope. Folders recurse into their children.
    /// </summary>
    private void WriteBucketInternal(
        string scope,
        string? parentContainer,
        IReadOnlyList<AdditionFolder> bucketFolders,
        IReadOnlyList<AdditionEntry> bucketEntries,
        AdditionState state,
        Dictionary<string, AdditionFolder> folderById,
        Dictionary<string, List<AdditionFolder>> foldersByParent,
        Dictionary<string, List<AdditionEntry>> entriesByFolder)
    {
        // Merged ordered view of items at this level. We use state.Folders /
        // state.Entries as the authoritative source for relative order: walk both
        // input lists in tandem? No — simpler: each item carries its position
        // within its own list, and we splice by interleaving in document order.
        // But documents only have two lists, so the cleanest model is:
        //   - Folders appear *first* in the bucket, then entries, within the same
        //     order as they appear in the respective lists. This matches RCMM v1
        //     behaviour and the mockup's left-pane rendering.
        var ordered = bucketFolders.Cast<object>().Concat(bucketEntries.Cast<object>()).ToList();

        // Ordinal is assigned PER SCOPE — it counts items that actually
        // participate in this scope-root. Items filtered out (entry whose Scope
        // != scope, or folder whose subtree touches no Scope-matching entry)
        // do NOT consume an ordinal slot. This way a bucket with mixed-scope
        // entries renders 001, 002 within each scope rather than skipping.
        int ord = 0;
        foreach (var item in ordered)
        {
            if (item is AdditionFolder f)
            {
                if (!FolderTouchesScope(f, scope, foldersByParent, entriesByFolder)) continue;
                ord++;
                WriteFolderRecursive(f, ord, scope, parentContainer, state, folderById, foldersByParent, entriesByFolder);
            }
            else if (item is AdditionEntry e)
            {
                if (!EntryScopePaths(e).Contains(scope, StringComparer.OrdinalIgnoreCase)) continue;
                ord++;
                WriteEntry(e, ord, scope, parentContainer);
            }
        }
    }

    private void WriteFolderRecursive(
        AdditionFolder folder,
        int ordinal,
        string scope,
        string? parentContainer,
        AdditionState state,
        Dictionary<string, AdditionFolder> folderById,
        Dictionary<string, List<AdditionFolder>> foldersByParent,
        Dictionary<string, List<AdditionEntry>> entriesByFolder)
    {
        // Caller has already verified FolderTouchesScope before bumping the
        // ordinal — if we got here, this folder participates in `scope`.
        var verbName = VerbName(ordinal, folder.Id);
        var verbPath = parentContainer == null
            ? $"{ClassesRoot}\\{scope}\\shell\\{verbName}"
            : $"{ClassesRoot}\\{parentContainer}\\shell\\{verbName}";
        var contextMenusPath = parentContainer == null
            ? $"{scope}\\ContextMenus\\{verbName}"
            : $"{parentContainer}\\ContextMenus\\{verbName}";

        _reg.SetValue(RegistryHive.CurrentUser, verbPath, "", folder.Name);
        var folderIcon = ResolveIcon(folder.Icon);
        if (!string.IsNullOrWhiteSpace(folderIcon))
            _reg.SetValue(RegistryHive.CurrentUser, verbPath, "Icon", folderIcon!);
        _reg.SetValue(RegistryHive.CurrentUser, verbPath, "ExtendedSubCommandsKey", contextMenusPath);

        // Recurse: write children of `folder` under `contextMenusPath`.
        var childFolders = foldersByParent.TryGetValue(folder.Id, out var cfs) ? (IReadOnlyList<AdditionFolder>)cfs : Array.Empty<AdditionFolder>();
        var childEntries = entriesByFolder.TryGetValue(folder.Id, out var ces) ? (IReadOnlyList<AdditionEntry>)ces : Array.Empty<AdditionEntry>();
        WriteBucketInternal(scope, contextMenusPath, childFolders, childEntries, state, folderById, foldersByParent, entriesByFolder);
    }

    /// <summary>
    /// Writes a single entry's keys. If <paramref name="parentContainer"/> is null
    /// the entry registers under <c>HKCU\Software\Classes\&lt;scope&gt;\shell\&lt;verbName&gt;</c>;
    /// otherwise under <c>HKCU\Software\Classes\&lt;parentContainer&gt;\shell\&lt;verbName&gt;</c>.
    /// Public for tests; production callers use Apply.
    /// </summary>
    public void WriteEntry(AdditionEntry entry, int ordinal, string scope, string? parentContainer)
    {
        var verbName = VerbName(ordinal, entry.Id);
        var commandText = WrapForRunMode(entry.RunMode, entry.Command, entry.Terminal);

        var resolvedIcon = ResolveIcon(entry.Icon);

        if (parentContainer != null)
        {
            var path = $"{ClassesRoot}\\{parentContainer}\\shell\\{verbName}";
            _reg.SetValue(RegistryHive.CurrentUser, path, "", entry.Name);
            if (!string.IsNullOrWhiteSpace(resolvedIcon))
                _reg.SetValue(RegistryHive.CurrentUser, path, "Icon", resolvedIcon!);
            _reg.SetValue(RegistryHive.CurrentUser, path + "\\command", "", commandText);
            return;
        }

        // Top-level: a File-scope entry expands to per-extension scope paths, so
        // emit once per matching scope. For non-File scopes, only the single root
        // applies, and we only get called for the correct scope from WriteBucketInternal.
        foreach (var scopePrefix in EntryScopePaths(entry))
        {
            if (entry.Scope == AdditionScope.File && !string.Equals(scopePrefix, scope, StringComparison.OrdinalIgnoreCase))
                continue;
            if (entry.Scope != AdditionScope.File && !string.Equals(scopePrefix, scope, StringComparison.OrdinalIgnoreCase))
                continue;
            var path = $"{ClassesRoot}\\{scopePrefix}\\shell\\{verbName}";
            _reg.SetValue(RegistryHive.CurrentUser, path, "", entry.Name);
            if (!string.IsNullOrWhiteSpace(resolvedIcon))
                _reg.SetValue(RegistryHive.CurrentUser, path, "Icon", resolvedIcon!);
            _reg.SetValue(RegistryHive.CurrentUser, path + "\\command", "", commandText);
        }
    }

    /// <summary>
    /// True iff <paramref name="folder"/> or any descendant entry participates
    /// in <paramref name="scope"/>. Used to skip writing an empty folder verb
    /// in a scope it has no business in.
    /// </summary>
    private static bool FolderTouchesScope(
        AdditionFolder folder, string scope,
        Dictionary<string, List<AdditionFolder>> foldersByParent,
        Dictionary<string, List<AdditionEntry>> entriesByFolder)
    {
        if (entriesByFolder.TryGetValue(folder.Id, out var es))
        {
            foreach (var e in es)
                if (EntryScopePaths(e).Contains(scope, StringComparer.OrdinalIgnoreCase)) return true;
        }
        if (foldersByParent.TryGetValue(folder.Id, out var cf))
        {
            foreach (var child in cf)
                if (FolderTouchesScope(child, scope, foldersByParent, entriesByFolder)) return true;
        }
        return false;
    }

    /// <summary>
    /// Collects every scope-root that at least one entry in the state uses,
    /// transitively across folders. Order: stable across runs so the resulting
    /// registry layout doesn't churn for cosmetic reasons.
    /// </summary>
    private static IReadOnlyList<string> CollectScopeRoots(AdditionState state, Dictionary<string, AdditionFolder> _byId)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in state.Entries)
            foreach (var s in EntryScopePaths(e))
                set.Add(s);
        return set.ToList();
    }

    private static Dictionary<string, List<AdditionFolder>> BuildFolderByParent(IReadOnlyList<AdditionFolder> folders)
    {
        var d = new Dictionary<string, List<AdditionFolder>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in folders)
        {
            var key = f.ParentFolderId ?? "";
            if (!d.TryGetValue(key, out var list)) { list = new List<AdditionFolder>(); d[key] = list; }
            list.Add(f);
        }
        return d;
    }

    private static Dictionary<string, List<AdditionEntry>> BuildEntriesByFolder(IReadOnlyList<AdditionEntry> entries)
    {
        var d = new Dictionary<string, List<AdditionEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            var key = e.FolderId ?? "";
            if (!d.TryGetValue(key, out var list)) { list = new List<AdditionEntry>(); d[key] = list; }
            list.Add(e);
        }
        return d;
    }

    /// <summary>
    /// Tears down every RCMM.-prefixed key under each known shell scope and any
    /// extra File-scope extension provided by the caller. Both \shell\RCMM.* and
    /// \ContextMenus\RCMM.* trees are removed. DeleteKey is recursive so nested
    /// folders' deeper ContextMenus subtrees go with their top-level parent.
    /// Non-prefixed keys are untouched.
    /// </summary>
    public void PurgeOwnedKeys(IEnumerable<string> extraExtensionRoots)
    {
        var roots = _staticScopeRoots.Concat(extraExtensionRoots.Select(NormaliseExtension))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int purged = 0;
        foreach (var root in roots)
        {
            purged += PurgePrefixed($"{ClassesRoot}\\{root}\\shell");
            purged += PurgePrefixed($"{ClassesRoot}\\{root}\\ContextMenus");
        }
        Log.Info(Cat, $"PurgeOwnedKeys purged={purged} roots={roots.Count}");
    }

    private int PurgePrefixed(string parentPath)
    {
        int count = 0;
        if (!_reg.KeyExists(RegistryHive.CurrentUser, parentPath)) return 0;
        foreach (var name in _reg.GetSubKeyNames(RegistryHive.CurrentUser, parentPath))
        {
            if (!name.StartsWith(VerbPrefix, StringComparison.Ordinal)) continue;
            _reg.DeleteKey(RegistryHive.CurrentUser, parentPath + "\\" + name);
            count++;
        }
        return count;
    }

    private static string NormaliseExtension(string ext)
        => ext.StartsWith('.') ? ext : "." + ext;
}
