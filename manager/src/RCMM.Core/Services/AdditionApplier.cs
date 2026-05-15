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

    public AdditionApplier(IRegistry reg) { _reg = reg; }

    /// <summary>
    /// Maps an AdditionScope to its registry path segment under
    /// HKCU\Software\Classes. File scope is special — it expands to either
    /// "*" or per-extension paths and is handled by <see cref="EntryScopePaths"/>.
    /// </summary>
    public static string ScopeRootFor(AdditionScope scope) => scope switch
    {
        AdditionScope.FolderBackground       => "Directory\\Background",
        AdditionScope.Folder                 => "Directory",
        AdditionScope.Drive                  => "Drive",
        AdditionScope.AllFilesystemObjects   => "AllFilesystemObjects",
        AdditionScope.File                   => "*",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
    };

    /// <summary>Transforms an entry's bare Command into the literal string written to the registry's command\(Default) value.</summary>
    public static string WrapForRunMode(RunMode mode, string command) => mode switch
    {
        RunMode.VisibleTerminal => "cmd /k " + command,
        RunMode.Background      => command,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>
    /// Returns the list of "&lt;scope-root&gt;" prefixes under which this entry registers.
    /// One element for most scopes; for File-with-extensions, one per extension.
    /// </summary>
    public static IReadOnlyList<string> EntryScopePaths(AdditionEntry entry)
    {
        if (entry.Scope != AdditionScope.File)
            return new[] { ScopeRootFor(entry.Scope) };
        if (entry.FileTypes == null || entry.FileTypes.Count == 0)
            return new[] { "*" };
        return entry.FileTypes.Select(ext => ext.StartsWith('.') ? ext : "." + ext).ToList();
    }

    /// <summary>
    /// Writes a single entry's keys. If <paramref name="parentContainer"/> is null the
    /// entry registers directly under HKCU\Software\Classes\&lt;scope&gt;\shell\RCMM.&lt;id&gt;.
    /// Otherwise it registers under HKCU\Software\Classes\&lt;parentContainer&gt;\shell\RCMM.&lt;id&gt; —
    /// used for child entries inside a folder's ContextMenus tree.
    /// </summary>
    public void WriteEntry(AdditionEntry entry, string? parentContainer)
    {
        var verbName = VerbPrefix + entry.Id;
        var commandText = WrapForRunMode(entry.RunMode, entry.Command);

        if (parentContainer != null)
        {
            var path = $"{ClassesRoot}\\{parentContainer}\\shell\\{verbName}";
            _reg.SetValue(RegistryHive.CurrentUser, path, "", entry.Name);
            if (!string.IsNullOrWhiteSpace(entry.Icon))
                _reg.SetValue(RegistryHive.CurrentUser, path, "Icon", entry.Icon!);
            _reg.SetValue(RegistryHive.CurrentUser, path + "\\command", "", commandText);
            return;
        }

        foreach (var scopePrefix in EntryScopePaths(entry))
        {
            var path = $"{ClassesRoot}\\{scopePrefix}\\shell\\{verbName}";
            _reg.SetValue(RegistryHive.CurrentUser, path, "", entry.Name);
            if (!string.IsNullOrWhiteSpace(entry.Icon))
                _reg.SetValue(RegistryHive.CurrentUser, path, "Icon", entry.Icon!);
            _reg.SetValue(RegistryHive.CurrentUser, path + "\\command", "", commandText);
        }
    }

    /// <summary>
    /// Writes a folder verb under every scope at least one of its children registers under.
    /// Each parent verb gets an ExtendedSubCommandsKey pointing at its scope-specific
    /// ContextMenus subtree, where the matching children's verbs live.
    /// </summary>
    public void WriteFolder(AdditionFolder folder, IReadOnlyCollection<AdditionEntry> children)
    {
        var verbName = VerbPrefix + folder.Id;
        var rootsUsed = children
            .SelectMany(EntryScopePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var root in rootsUsed)
        {
            var parentPath = $"{ClassesRoot}\\{root}\\shell\\{verbName}";
            var contextMenusPath = $"{root}\\ContextMenus\\{verbName}";
            _reg.SetValue(RegistryHive.CurrentUser, parentPath, "", folder.Name);
            if (!string.IsNullOrWhiteSpace(folder.Icon))
                _reg.SetValue(RegistryHive.CurrentUser, parentPath, "Icon", folder.Icon!);
            _reg.SetValue(RegistryHive.CurrentUser, parentPath, "ExtendedSubCommandsKey", contextMenusPath);

            foreach (var child in children)
            {
                var childRoots = EntryScopePaths(child);
                if (!childRoots.Contains(root, StringComparer.OrdinalIgnoreCase)) continue;
                WriteEntry(child, parentContainer: contextMenusPath);
            }
        }
    }

    /// <summary>
    /// Tears down every RCMM.-prefixed key under each known shell scope and any
    /// extra File-scope extension provided by the caller. Both \shell\RCMM.* and
    /// \ContextMenus\RCMM.* trees are removed. Non-prefixed keys are untouched.
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

    /// <summary>
    /// Idempotent full rebuild: purge every existing RCMM.* registration we own,
    /// then write the supplied state from scratch. Caller is responsible for
    /// persisting the AdditionState to disk separately (so a failed registry
    /// write leaves both file and registry on the previous state).
    /// </summary>
    public void Apply(AdditionState state)
    {
        Log.Info(Cat, $"Apply begin entries={state.Entries.Count} folders={state.Folders.Count}");

        // Collect all File-scope extensions used by any entry so PurgeOwnedKeys
        // also cleans those scope-roots. Without this a previously-applied
        // .png entry would survive a state that no longer references .png.
        var extraExts = state.Entries
            .Where(e => e.Scope == AdditionScope.File && e.FileTypes is { Count: > 0 })
            .SelectMany(e => e.FileTypes!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        PurgeOwnedKeys(extraExts);

        var folderById = state.Folders.ToDictionary(f => f.Id, f => f);
        var topLevel = state.Entries.Where(e => string.IsNullOrEmpty(e.FolderId)).ToList();
        var byFolder = state.Entries
            .Where(e => !string.IsNullOrEmpty(e.FolderId))
            .GroupBy(e => e.FolderId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var entry in topLevel)
            WriteEntry(entry, parentContainer: null);

        foreach (var folder in state.Folders)
        {
            var children = byFolder.TryGetValue(folder.Id, out var list) ? list : new List<AdditionEntry>();
            if (children.Count == 0)
            {
                Log.Debug(Cat, $"folder {folder.Name} has no children — skipping registry write");
                continue;
            }
            WriteFolder(folder, children);
        }

        // Orphan entries (FolderId set but folder missing) — treat as top-level.
        var orphans = state.Entries
            .Where(e => !string.IsNullOrEmpty(e.FolderId) && !folderById.ContainsKey(e.FolderId!))
            .ToList();
        foreach (var orphan in orphans)
        {
            Log.Warn(Cat, $"entry {orphan.Name} references missing folder {orphan.FolderId} — writing as top-level");
            WriteEntry(orphan, parentContainer: null);
        }

        Log.Info(Cat, "Apply end");
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
