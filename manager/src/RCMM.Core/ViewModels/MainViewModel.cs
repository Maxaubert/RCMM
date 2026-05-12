using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using RCMM.Core.Diagnostics;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Core.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IContextMenuCaptureService _capture;
    private readonly TargetProvider _targets;
    private readonly VerbToRegistryMapper _mapper;
    private readonly HideService _hideService;
    private readonly IRegistry _reg;
    private readonly IFileVersionReader _files;
    private readonly ShellexNameIndex _shellexIndex;
    private readonly EntryScanner? _registryScanner;
    private readonly PackagedShellExtScanner? _packagedScanner;
    private readonly CommandStoreVerbIndex? _commandStore;
    private readonly ShellexKeyNameIndex? _shellexKey;
    private readonly ShellexInvoker? _shellexInvoker;

    private readonly HashSet<string> _packagedClsids =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _packagedPublishers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _packagedDllByClsid =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _observedClsids =
        new(StringComparer.OrdinalIgnoreCase);


    private readonly Dictionary<string, IReadOnlyList<HideTarget>> _pendingHide = new();
    private readonly Dictionary<string, IReadOnlyList<HideTarget>> _pendingUnhide = new();
    private readonly List<EntryRowViewModel> _allRows = new();
    private bool _showBuiltIns = true;

    public ObservableCollection<EntryRowViewModel> AllEntries { get; } = new();
    public ObservableCollection<string> PendingChangeIds { get; } = new();

    public MainViewModel(
        IContextMenuCaptureService capture,
        TargetProvider targets,
        VerbToRegistryMapper mapper,
        HideService hideService,
        IRegistry reg,
        IFileVersionReader files,
        ShellexNameIndex shellexIndex,
        EntryScanner? registryScanner = null,
        PackagedShellExtScanner? packagedScanner = null,
        CommandStoreVerbIndex? commandStore = null,
        ShellexKeyNameIndex? shellexKey = null,
        ShellexInvoker? shellexInvoker = null)
    {
        _capture = capture;
        _targets = targets;
        _mapper = mapper;
        _hideService = hideService;
        _reg = reg;
        _files = files;
        _shellexIndex = shellexIndex;
        _registryScanner = registryScanner;
        _packagedScanner = packagedScanner;
        _commandStore = commandStore;
        _shellexKey = shellexKey;
        _shellexInvoker = shellexInvoker;
    }

    public bool RequiresExplorerRestart
        => _pendingHide.Values.Concat(_pendingUnhide.Values)
                       .SelectMany(t => t)
                       .Any(t => t.Kind == HideKind.HkcuMask || t.Kind == HideKind.BlockedShellExt);

    public bool ShowBuiltIns
    {
        get => _showBuiltIns;
        set
        {
            if (SetField(ref _showBuiltIns, value))
                FilterIntoAllEntries();
        }
    }

    public void Rescan()
    {
        Log.Info("rescan", "begin");
        var targets = _targets.GetTargets();
        Log.Debug("rescan", $"targets={targets.Count}");
        var allItems = new List<CapturedItem>();
        allItems.AddRange(_capture.CaptureAll(targets));
        int liveCount = allItems.Count;

        // Packaged COM context menus go BEFORE the classic registry scan because
        // (a) their friendly DisplayName is more accurate than a DLL's FileDescription
        // and (b) they own the BlockedShellExt hide-target path; ResolveHideTargets
        // consults _packagedClsids to pick the right hide path.
        _packagedClsids.Clear();
        _packagedPublishers.Clear();
        _packagedDllByClsid.Clear();
        if (_packagedScanner != null)
        {
            // Register packaged CLSIDs with the invoker BEFORE the packaged scan so
            // it can probe IExplorerCommand for the friendly title (e.g.
            // "AMD Software: Adrenalin Edition" instead of "Catalyst Context Menu
            // extension") which we then use as the row's DisplayName.
            var pkgList = _packagedScanner.Scan().ToList();
            if (_shellexInvoker != null)
                foreach (var pkg in pkgList)
                    _shellexInvoker.RegisterExtraClsid(pkg.Clsid);
            _shellexInvoker?.BuildDisplayNameToClsidMap(); // forces probe so titles are ready

            int pos = 0;
            int packagedAdded = 0;
            foreach (var pkg in pkgList)
            {
                _packagedClsids.Add(pkg.Clsid);
                if (!_packagedPublishers.ContainsKey(pkg.Clsid))
                    _packagedPublishers[pkg.Clsid] = pkg.PublisherDisplayName;
                if (pkg.DllPath != null && !_packagedDllByClsid.ContainsKey(pkg.Clsid))
                    _packagedDllByClsid[pkg.Clsid] = pkg.DllPath;

                // Display name priority:
                //   1. IExplorerCommand::GetTitle — the exact menu text the shell renders.
                //   2. PublisherDisplayName ("AMD Software", "Notepad++", "WinRAR") —
                //      recognisable to the user, way cleaner than the technical
                //      class label ("Catalyst Context Menu extension",
                //      "WindowsTerminalShellExt").
                //   3. Registry-reported DisplayName as a last resort.
                var liveTitle = _shellexInvoker?.LookupTitle(pkg.Clsid);
                var display = !string.IsNullOrWhiteSpace(liveTitle) ? liveTitle!
                            : LooksTechnical(pkg.DisplayName) ? pkg.PublisherDisplayName
                            : pkg.DisplayName;

                allItems.Add(new CapturedItem
                {
                    TargetPath = $"<packaged:{pkg.PackageFullName}>",
                    Position = pos++,
                    DisplayName = display,
                    OwnerClsid = pkg.Clsid,
                    IsSeparator = false,
                    IsSubmenu = false
                });
                packagedAdded++;
            }
            Log.Info("rescan", $"packagedAdded={packagedAdded} clsids={_packagedClsids.Count}");
        }

        if (_registryScanner != null)
        {
            allItems.AddRange(_registryScanner.ScanAsCaptures());
            Log.Info("rescan", $"liveCaptured={liveCount} packaged+registry={allItems.Count - liveCount}");
        }
        var nameIndex = _shellexIndex.BuildNameToClsidMap();
        var wordIndex = _shellexIndex.BuildClsidWordIndex();
        // Track which CLSIDs we know are "active" — either attached to a live
        // capture, or the invoker recorded the handler emitting menu items during
        // its probe. Registry-derived rows whose CLSID is neither are filtered out
        // by FilterIntoAllEntries as they correspond to shellexes that don't
        // contribute to any sample right-click menu (Launches Sync Center,
        // Work Folders, Client Side Caching UI, etc.).
        _observedClsids.Clear();
        if (_shellexInvoker != null)
        {
            // Force probe so emitted names are available now.
            _shellexInvoker.BuildDisplayNameToClsidMap();
        }

        // Pre-merge pass:
        //   (a) Attach OwnerClsid to live captures so the merge's clsid-key dedup
        //       collapses a live "Scan for deleted files" with the registry-derived
        //       "Recuva shell extensions" row (same CLSID).
        //   (b) For any item whose DisplayName is a technical FileDescription
        //       label and whose CLSID was probed, replace DisplayName with the
        //       first emitted menu text. "Microsoft Security Client Shell
        //       Extension" → "Scan with Microsoft Defender…" when Defender's
        //       handler emitted that text during the invoker run.
        for (int i = 0; i < allItems.Count; i++)
        {
            var item = allItems[i];
            if (string.IsNullOrEmpty(item.OwnerClsid))
            {
                if (nameIndex.TryGetValue(item.DisplayName, out var nameClsid))
                    item = item with { OwnerClsid = nameClsid };
                else
                {
                    var fuzzy = _shellexIndex.FuzzyMatch(item.DisplayName, wordIndex);
                    if (fuzzy != null) item = item with { OwnerClsid = fuzzy };
                    else
                    {
                        var invMap = _shellexInvoker?.BuildDisplayNameToClsidMap();
                        if (invMap != null && invMap.TryGetValue(item.DisplayName, out var invClsid))
                            item = item with { OwnerClsid = invClsid };
                    }
                }
            }

            if (!string.IsNullOrEmpty(item.OwnerClsid) && LooksTechnical(item.DisplayName))
            {
                var emitted = _shellexInvoker?.LookupEmittedNames(item.OwnerClsid)
                    .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n) && !LooksTechnical(n));
                if (!string.IsNullOrWhiteSpace(emitted))
                    item = item with { DisplayName = emitted! };
            }

            // A CLSID counts as "observed" only when it's attached to a *live*
            // captured menu item (TargetPath is a real path, not a "<registry:..>"
            // or "<packaged:..>" placeholder). Invoker emissions don't qualify
            // because some shellexes (Work Folders, Client Side Caching UI,
            // Launches Sync Center, …) emit informational items during a probe
            // but never contribute to the actual right-click menu the user sees.
            if (!string.IsNullOrEmpty(item.OwnerClsid) && !item.TargetPath.StartsWith("<"))
                _observedClsids.Add(item.OwnerClsid!);

            allItems[i] = item;
        }

        var merged = MergeCaptures(allItems).ToList();
        Log.Info("rescan", $"captured={allItems.Count} mergedUnique={merged.Count}");

        // Feed the invoker every handler CLSID we know about — verb
        // ExplorerCommandHandlers / VerbHandlers, CommandStore handlers, packaged
        // CLSIDs — so its IExplorerCommand::GetIcon pass can resolve their icons.
        if (_shellexInvoker != null)
        {
            foreach (var captured in allItems)
                if (!string.IsNullOrEmpty(captured.OwnerClsid))
                    _shellexInvoker.RegisterExtraClsid(captured.OwnerClsid!);
            foreach (var captured in allItems)
            {
                if (string.IsNullOrEmpty(captured.Verb)) continue;
                foreach (var cmdStoreClsid in _commandStore?.LookupClsids(captured.Verb!) ?? Array.Empty<string>())
                    _shellexInvoker.RegisterExtraClsid(cmdStoreClsid);
            }
            // Also register handler CLSIDs from each verb registration. Walk MapVerb
            // for every captured verb and read the handler-CLSID fields out of HKCR.
            foreach (var captured in allItems)
            {
                if (string.IsNullOrEmpty(captured.Verb)) continue;
                foreach (var t in _mapper.MapVerb(captured.Verb!))
                {
                    var hkcrPath = HkcrPathFor(t) ?? t.Path;
                    foreach (var field in new[] { "ExplorerCommandHandler", "VerbHandler", "CommandStateHandler", "CanonicalName" })
                        if (_reg.GetValue(RegistryHive.ClassesRoot, hkcrPath, field) is string c && LooksLikeClsid(c))
                            _shellexInvoker.RegisterExtraClsid(c);
                }
            }
        }

        // Heaviest lookup last so cheaper resolvers win: instantiate each shellex
        // handler and ask what it emits. Catches Recuva / Library Location / PlayTo,
        // which have no name overlap with their FileDescription. Cached per-process.
        var invokerMap = _shellexInvoker?.BuildDisplayNameToClsidMap();
        Log.Debug("rescan", $"shellexNameIndex entries={nameIndex.Count} wordIndexClsids={wordIndex.Count} invokerNames={invokerMap?.Count ?? 0}");

        int rowsWithHide = 0;
        int rowsBuiltIn = 0;
        _allRows.Clear();
        foreach (var item in merged)
        {
            var effectiveItem = item;  // pre-attached above
            var hideTargets = ResolveHideTargets(effectiveItem);
            var iconPath = ResolveIconPath(effectiveItem, hideTargets);
            var isHidden = AllTargetsHidden(hideTargets);
            var (source, isBuiltIn) = ResolveSourceAndBuiltIn(effectiveItem, hideTargets);
            var entry = new MenuEntry
            {
                Id = ComputeId(effectiveItem),
                DisplayName = effectiveItem.DisplayName,
                Source = source,
                IconBytes = effectiveItem.IconBytes,
                IconPath = iconPath,
                HideTargets = hideTargets,
                IsBuiltIn = isBuiltIn,
                IsHidden = isHidden,
                IsSubmenu = effectiveItem.IsSubmenu
            };
            var row = new EntryRowViewModel(entry) { HiddenChanged = OnRowToggled };
            _allRows.Add(row);
            if (hideTargets.Count > 0) rowsWithHide++;
            if (isBuiltIn) rowsBuiltIn++;
        }

        // Second-pass dedup: a registry-derived or packaged row whose technical
        // DisplayName has been renamed via IExplorerCommand::GetTitle (or that
        // simply coincides with a live captured row) now shares its display name
        // with the live row. Collapse them — keep the live row (prefer the one
        // with a Verb), union the hide targets.
        DeduplicateRowsByDisplayName();

        FilterIntoAllEntries();
        _pendingHide.Clear();
        _pendingUnhide.Clear();
        PendingChangeIds.Clear();
        Raise(nameof(RequiresExplorerRestart));
        Log.Info("rescan", $"end rows={_allRows.Count} withHideTargets={rowsWithHide} builtIn={rowsBuiltIn} visible={AllEntries.Count}");
        for (int i = 0; i < _allRows.Count; i++)
        {
            var r = _allRows[i];
            var src = string.IsNullOrEmpty(r.Entry.Source) ? "Unknown" : r.Entry.Source;
            Log.Debug("dump", $"#{i:D2} '{r.Entry.DisplayName}' src='{src}' sub={r.Entry.IsSubmenu} hideTargets={r.Entry.HideTargets.Count} icon='{r.Entry.IconPath ?? ""}'");
        }
    }

    private void FilterIntoAllEntries()
    {
        AllEntries.Clear();
        foreach (var row in _allRows)
        {
            if (row.IsBuiltIn && !_showBuiltIns) continue;
            // Hide rows whose DisplayName is a technical class label — those are
            // FileDescriptions like "Windows Shell Common Dll" / "Microsoft Security
            // Client Shell Extension" that don't correspond to a real menu option.
            if (LooksTechnical(row.Entry.DisplayName)) continue;
            // Hide CLSID-keyed rows that are neither packaged nor "observed" — i.e.,
            // shellexes whose handler wasn't attached to any live capture AND didn't
            // emit anything during invoker probing. These are conditional handlers
            // ("Launches Sync Center.", "Work Folders", "Client Side Caching UI",
            // BitLocker variants) that never appear in standard right-click menus.
            if (row.Entry.Id.StartsWith("clsid:", StringComparison.Ordinal))
            {
                var clsid = row.Entry.Id.Substring("clsid:".Length);
                if (!_packagedClsids.Contains(clsid) && !_observedClsids.Contains(clsid))
                    continue;
            }
            AllEntries.Add(row);
        }
    }

    /// <summary>
    /// Walks <see cref="_allRows"/>, groups by case-insensitive DisplayName, and
    /// collapses each group into a single row. The winner is the row most likely
    /// to be what the user actually sees in their menu — preferring rows with a
    /// classic Verb (live capture), then rows with a non-Unknown Source, then the
    /// first. Hide targets from every dropped row are concatenated onto the
    /// winner so toggling it suppresses every underlying handler at once.
    /// </summary>
    private void DeduplicateRowsByDisplayName()
    {
        if (_allRows.Count < 2) return;
        var groups = new Dictionary<string, List<EntryRowViewModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _allRows)
        {
            if (!groups.TryGetValue(r.Entry.DisplayName, out var list))
                groups[r.Entry.DisplayName] = list = new();
            list.Add(r);
        }

        int collapsed = 0;
        foreach (var (_, group) in groups)
        {
            if (group.Count < 2) continue;
            var winner = PickDedupWinner(group);
            var unionTargets = new List<HideTarget>(winner.Entry.HideTargets);
            var seen = new HashSet<(HideKind, RegistryHive, string, string?)>(
                winner.Entry.HideTargets.Select(t => (t.Kind, t.Hive, t.Path, t.ValueName)));
            foreach (var r in group)
            {
                if (ReferenceEquals(r, winner)) continue;
                foreach (var t in r.Entry.HideTargets)
                    if (seen.Add((t.Kind, t.Hive, t.Path, t.ValueName)))
                        unionTargets.Add(t);
                _allRows.Remove(r);
                collapsed++;
            }

            // Rebuild the winner's MenuEntry with merged hide targets so the toggle
            // semantics (CanHide, IsHidden) reflect the combined state.
            var newEntry = winner.Entry with
            {
                HideTargets = unionTargets,
                IsHidden = AllTargetsHidden(unionTargets)
            };
            var idx = _allRows.IndexOf(winner);
            var rebuilt = new EntryRowViewModel(newEntry) { HiddenChanged = OnRowToggled };
            // Preserve any icon already loaded on the winner.
            rebuilt.Icon = winner.Icon;
            _allRows[idx] = rebuilt;
        }
        if (collapsed > 0) Log.Info("rescan", $"dedupeByDisplayName collapsed={collapsed}");
    }

    private static EntryRowViewModel PickDedupWinner(List<EntryRowViewModel> group)
    {
        // Prefer a row whose Id key is a "verb:..." entry — that came from a live
        // captured menu item and is the closest to what the user actually sees.
        var live = group.FirstOrDefault(r => r.Entry.Id.StartsWith("verb:", StringComparison.Ordinal));
        if (live != null) return live;
        // Otherwise prefer one with a resolved (non-Unknown) source.
        var named = group.FirstOrDefault(r => !string.IsNullOrEmpty(r.Entry.Source));
        return named ?? group[0];
    }

    private static IEnumerable<CapturedItem> MergeCaptures(IEnumerable<CapturedItem> captures)
    {
        var seen = new HashSet<string>();
        foreach (var item in captures)
        {
            if (item.IsSeparator) continue;
            var key = MergeKey(item);
            if (seen.Add(key)) yield return item;
        }
    }

    private static string MergeKey(CapturedItem item)
    {
        if (!string.IsNullOrEmpty(item.Verb)) return "verb:" + item.Verb!.ToLowerInvariant();
        if (!string.IsNullOrEmpty(item.OwnerClsid)) return "clsid:" + item.OwnerClsid!.ToLowerInvariant();
        return "name:" + item.DisplayName.ToLowerInvariant();
    }

    private static string ComputeId(CapturedItem item) => MergeKey(item);

    private IReadOnlyList<HideTarget> ResolveHideTargets(CapturedItem item)
    {
        var result = new List<HideTarget>();
        if (!string.IsNullOrEmpty(item.Verb))
        {
            result.AddRange(_mapper.MapVerb(item.Verb!));

            // Windows' built-in verbs (Share, Open with, Copy as path, …) live in
            // CommandStore — they don't have a per-scope <scope>\shell\<verb> key so
            // MapVerb finds nothing. The hide path is to block every handler CLSID
            // CommandStore lists for the verb via the Shell Extensions\Blocked list.
            if (_commandStore != null)
                foreach (var c in _commandStore.LookupClsids(item.Verb!))
                    result.Add(HideService.BlockedShellExtTarget(c));
        }
        if (!string.IsNullOrEmpty(item.OwnerClsid))
        {
            int before = result.Count;
            result.AddRange(_mapper.MapClsid(item.OwnerClsid!));
            bool packaged = _packagedClsids.Contains(item.OwnerClsid!);
            bool noClassicMatch = result.Count == before;
            // Packaged extensions always also get the Blocked-list target. For classic
            // shellex CLSIDs whose registration lives outside our standard six scopes
            // (PlayTo on Stack.Audio/Image/Video, etc.), the Blocked list is the
            // universal fallback — Explorer honours it for any shell extension CLSID.
            if (packaged || noClassicMatch)
                result.Add(HideService.BlockedShellExtTarget(item.OwnerClsid!));
        }

        // Items captured live with no verb and no CLSID (e.g. "Send to", "Open with")
        // can sometimes be matched to their shellex handler by display-name similarity
        // to the shellex registration key name.
        if (result.Count == 0 && _shellexKey != null && !string.IsNullOrEmpty(item.DisplayName))
            result.AddRange(_shellexKey.MapDisplayName(item.DisplayName));

        return result;
    }

    private string? ResolveIconPath(CapturedItem item, IReadOnlyList<HideTarget> targets)
    {
        // 0. IExplorerCommand::GetIcon for any handler we've probed. Modern verbs
        // (Notepad++'s ANotepad++64, packaged context menus, many CommandStore
        // entries) implement IExplorerCommand and return an explicit icon path
        // pointing at the real product binary (notepad++.exe etc.). This is the
        // highest-fidelity source we have outside of intercepting the menu's HBITMAP.
        if (_shellexInvoker != null)
        {
            // Check the OwnerClsid first.
            var byClsid = _shellexInvoker.LookupIconPath(item.OwnerClsid);
            if (!string.IsNullOrWhiteSpace(byClsid)) return byClsid;
            // Then any handler CLSID registered against the verb.
            if (!string.IsNullOrEmpty(item.Verb))
            {
                foreach (var t in targets)
                {
                    if (t.Kind != HideKind.LegacyDisable) continue;
                    var hkcrPath = HkcrPathFor(t) ?? t.Path;
                    foreach (var field in new[] { "ExplorerCommandHandler", "VerbHandler", "CommandStateHandler", "CanonicalName" })
                    {
                        if (_reg.GetValue(RegistryHive.ClassesRoot, hkcrPath, field) is string c && LooksLikeClsid(c))
                        {
                            var icon = _shellexInvoker.LookupIconPath(c);
                            if (!string.IsNullOrWhiteSpace(icon)) return icon;
                        }
                    }
                }
                if (_commandStore != null)
                {
                    foreach (var c in _commandStore.LookupClsids(item.Verb!))
                    {
                        var icon = _shellexInvoker.LookupIconPath(c);
                        if (!string.IsNullOrWhiteSpace(icon)) return icon;
                    }
                }
            }
        }

        // 1. Verb icon registered alongside the verb itself. Hide targets live in
        // HKCU\Software\Classes\…, but the Icon and command values are at HKLM —
        // read the merged HKCR view so installed-app verbs (VLC, Notepad++, Git, …)
        // surface their custom icons.
        foreach (var t in targets)
        {
            if (t.Kind != HideKind.LegacyDisable) continue;
            var hkcrPath = HkcrPathFor(t) ?? t.Path;
            var icon = _reg.GetValue(RegistryHive.ClassesRoot, hkcrPath, "Icon") as string;
            if (!string.IsNullOrWhiteSpace(icon)) return icon;
            var cmd = _reg.GetValue(RegistryHive.ClassesRoot, hkcrPath + @"\command", "") as string;
            if (!string.IsNullOrWhiteSpace(cmd)) return cmd;

            // Verbs that delegate to a COM handler (modern Notepad++, pintohomefile, …)
            // expose the handler's CLSID through one of these fields. We use the
            // handler DLL as a last-ditch icon source — but only when the DLL is
            // NOT in System32. Microsoft system DLLs (shell32, windows.storage, …)
            // contain hundreds of icons and the first one is almost always a
            // generic placeholder; the shell uses an HBITMAP the handler emits at
            // runtime, which we can't read from the registry.
            foreach (var field in new[] { "ExplorerCommandHandler", "VerbHandler", "CommandStateHandler", "CanonicalName" })
            {
                if (_reg.GetValue(RegistryHive.ClassesRoot, hkcrPath, field) is string handlerClsid
                    && LooksLikeClsid(handlerClsid))
                {
                    var dll = _reg.GetValue(RegistryHive.ClassesRoot, $@"CLSID\{handlerClsid}\InprocServer32", "") as string;
                    if (!IsSystemDll(dll) && !string.IsNullOrWhiteSpace(dll)) return dll;
                }
            }
            if (_reg.GetValue(RegistryHive.ClassesRoot, hkcrPath + @"\command", "DelegateExecute") is string delegateClsid
                && LooksLikeClsid(delegateClsid))
            {
                var dll = _reg.GetValue(RegistryHive.ClassesRoot, $@"CLSID\{delegateClsid}\InprocServer32", "") as string;
                if (!IsSystemDll(dll) && !string.IsNullOrWhiteSpace(dll)) return dll;
            }
        }

        // 2. CommandStore icon for Windows' built-in verbs (Share, Open with, Copy
        // as path, Cut, Copy, Delete, Properties, …). These reference resource IDs
        // inside imageres.dll / shell32.dll — IconHelper handles the `,-NNNN` form.
        if (!string.IsNullOrEmpty(item.Verb) && _commandStore != null)
        {
            var hint = _commandStore.LookupIcon(item.Verb!);
            if (!string.IsNullOrWhiteSpace(hint)) return hint;
        }

        // 3. CLSID-owned icons — shellex handlers (Recuva, Defender, ModernSharing,
        // PlayTo, …) typically expose their icon via DefaultIcon or as the first
        // icon resource of the registered DLL. Skip system DLL fallbacks because
        // their first icon is almost always a generic placeholder.
        if (!string.IsNullOrEmpty(item.OwnerClsid))
        {
            var clsidPath = $@"CLSID\{item.OwnerClsid}";
            var defaultIcon = _reg.GetValue(RegistryHive.ClassesRoot, clsidPath + @"\DefaultIcon", "") as string;
            if (!string.IsNullOrWhiteSpace(defaultIcon)) return defaultIcon;
            var dll = _reg.GetValue(RegistryHive.ClassesRoot, clsidPath + @"\InprocServer32", "") as string;
            if (!string.IsNullOrWhiteSpace(dll) && !IsSystemDll(dll)) return dll;

            // Packaged COM extensions aren't in classic HKCR\CLSID; their DLL lives
            // under C:\Program Files\WindowsApps\<package>\… resolved from the AppX
            // store and PackagedCom\Package\<pkg>\Class\<CLSID>\DllPath.
            if (_packagedDllByClsid.TryGetValue(item.OwnerClsid!, out var packagedDll))
                return packagedDll;
        }

        return null;
    }

    /// <summary>
    /// True when the path resolves to a DLL inside Windows\System32 or SysWOW64.
    /// Used as an icon-source filter: the shell's generic DLLs (shell32.dll,
    /// windows.storage.dll, ntshrui.dll, …) host hundreds of icons and the first
    /// one is a placeholder, so the menu's real icon comes from a runtime HBITMAP
    /// the handler emits — not from index 0 of the DLL.
    /// </summary>
    private static bool IsSystemDll(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var s = Environment.ExpandEnvironmentVariables(path).ToLowerInvariant();
            if (s.StartsWith('"'))
            {
                var end = s.IndexOf('"', 1);
                if (end > 1) s = s[1..end];
            }
            var comma = s.LastIndexOf(',');
            if (comma > 0 && comma > s.LastIndexOf('\\')) s = s[..comma];
            return s.Contains(@"\windows\system32\")
                || s.Contains(@"\windows\syswow64\");
        }
        catch { return false; }
    }

    private bool AllTargetsHidden(IReadOnlyList<HideTarget> targets)
    {
        if (targets.Count == 0) return false;
        foreach (var t in targets)
        {
            switch (t.Kind)
            {
                case HideKind.LegacyDisable:
                    // LegacyDisable writes go to HKCU\Software\Classes\..., but it's
                    // also valid for an admin to have written it into HKLM directly.
                    // Read the merged HKCR view so both surface as "hidden".
                    var hkcrPath = HkcrPathFor(t);
                    if (hkcrPath != null && _reg.GetValue(RegistryHive.ClassesRoot, hkcrPath, t.ValueName ?? "LegacyDisable") != null) break;
                    if (_reg.GetValue(t.Hive, t.Path, t.ValueName ?? "LegacyDisable") == null) return false;
                    break;
                case HideKind.HkcuMask:
                    if (!_reg.KeyExists(t.Hive, t.Path)) return false;
                    break;
                case HideKind.BlockedShellExt:
                    if (_reg.GetValue(t.Hive, t.Path, t.ValueName!) == null) return false;
                    break;
            }
        }
        return true;
    }

    private static bool LooksLikeClsid(string s)
        => !string.IsNullOrEmpty(s) && s.Length >= 38 && s.StartsWith('{') && s.EndsWith('}');

    /// <summary>
    /// Heuristic for a "technical" DisplayName the user wouldn't recognise as a menu item:
    /// CamelCase identifiers with no spaces, or substrings like "Shell Extension" /
    /// "Common Dll" / "Property Page" / "Context Menu extension" / "verb handler".
    /// Used to decide whether to swap in the publisher's friendly product name.
    /// </summary>
    private static bool LooksTechnical(string s)
    {
        if (string.IsNullOrEmpty(s)) return true;
        var lower = s.ToLowerInvariant();
        if (lower.Contains("shell extension") || lower.Contains("shellext")
            || lower.Contains("shell common dll") || lower.Contains("common dll")
            || lower.Contains("property page") || lower.Contains("context menu extension")
            || lower.Contains("verb handler") || lower.Contains("context menu verb handler"))
            return true;
        // CamelCase identifier (one token, mixed case)
        if (!s.Contains(' ') && s.Length >= 8)
        {
            bool hasLower = false, hasUpper = false;
            foreach (var c in s)
            {
                if (char.IsLower(c)) hasLower = true;
                else if (char.IsUpper(c)) hasUpper = true;
            }
            if (hasLower && hasUpper) return true;
        }
        return false;
    }

    /// <summary>
    /// Maps an HKCU\Software\Classes\... HideTarget path back to its HKCR equivalent
    /// so we can read the merged view (HKLM ∪ HKCU) when checking hide state.
    /// </summary>
    private static string? HkcrPathFor(HideTarget t)
    {
        if (t.Hive == RegistryHive.CurrentUser
            && t.Path.StartsWith(@"Software\Classes\", StringComparison.OrdinalIgnoreCase))
        {
            return t.Path.Substring(@"Software\Classes\".Length);
        }
        return null;
    }

    private void OnRowToggled(EntryRowViewModel row, bool isHidden)
    {
        var id = row.Entry.Id;
        var currentlyHidden = AllTargetsHidden(row.Entry.HideTargets);
        Log.Debug("toggle", $"id='{id}' name='{row.Entry.DisplayName}' newIsHidden={isHidden} currentlyHidden={currentlyHidden} targets={row.Entry.HideTargets.Count}");
        if (isHidden == currentlyHidden)
        {
            _pendingHide.Remove(id);
            _pendingUnhide.Remove(id);
            PendingChangeIds.Remove(id);
        }
        else if (isHidden)
        {
            _pendingHide[id] = row.Entry.HideTargets;
            _pendingUnhide.Remove(id);
            if (!PendingChangeIds.Contains(id)) PendingChangeIds.Add(id);
        }
        else
        {
            _pendingUnhide[id] = row.Entry.HideTargets;
            _pendingHide.Remove(id);
            if (!PendingChangeIds.Contains(id)) PendingChangeIds.Add(id);
        }
        Raise(nameof(RequiresExplorerRestart));
    }

    public void ApplyPending()
    {
        Log.Info("apply", $"begin hide={_pendingHide.Count} unhide={_pendingUnhide.Count}");
        foreach (var (id, targets) in _pendingHide)
        {
            Log.Debug("apply", $"hide id='{id}' targets={targets.Count}");
            foreach (var t in targets)
                Log.Debug("apply", $"  hide  kind={t.Kind} hive={t.Hive} path='{t.Path}' value='{t.ValueName}'");
            try { _hideService.Hide(targets); }
            catch (Exception ex) { Log.Error("apply", $"hide id={id} failed", ex); }
        }
        foreach (var (id, targets) in _pendingUnhide)
        {
            Log.Debug("apply", $"unhide id='{id}' targets={targets.Count}");
            foreach (var t in targets)
                Log.Debug("apply", $"  unhide kind={t.Kind} hive={t.Hive} path='{t.Path}' value='{t.ValueName}'");
            try { _hideService.Unhide(targets); }
            catch (Exception ex) { Log.Error("apply", $"unhide id={id} failed", ex); }
        }
        _pendingHide.Clear();
        _pendingUnhide.Clear();
        PendingChangeIds.Clear();
        Raise(nameof(RequiresExplorerRestart));
        Log.Info("apply", "end");
    }

    private (string? source, bool isBuiltIn) ResolveSourceAndBuiltIn(CapturedItem item, IReadOnlyList<HideTarget> targets)
    {
        string? exePath = null;
        string? clsidForFile = item.OwnerClsid;

        foreach (var t in targets)
        {
            if (t.Kind != HideKind.LegacyDisable) continue;
            var cmd = _reg.GetValue(t.Hive, t.Path + @"\command", "") as string;
            if (!string.IsNullOrWhiteSpace(cmd))
            {
                exePath = ExtractExe(cmd);
                break;
            }
        }

        // For shellex items, look up the DLL via the CLSID.
        string? dllPath = null;
        if (clsidForFile != null)
        {
            dllPath = _reg.GetValue(RegistryHive.ClassesRoot, $@"CLSID\{clsidForFile}\InprocServer32", "") as string;
        }

        var probe = exePath ?? dllPath;
        if (!string.IsNullOrEmpty(probe))
        {
            var info = _files.Read(probe);
            var company = info.CompanyName;
            bool builtIn = (company != null && company.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0)
                            || LooksWindowsPath(probe);
            return (company, builtIn);
        }

        // Packaged extensions don't have a classic CLSID InprocServer entry. Use the
        // publisher name straight from the AppX registration so the user can recognise
        // which app the entry belongs to (e.g. "AMD Software", "Microsoft").
        if (clsidForFile != null && _packagedPublishers.TryGetValue(clsidForFile, out var pub))
        {
            bool builtIn = pub.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;
            return (pub, builtIn);
        }
        return (null, false);
    }

    private static string? ExtractExe(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            if (end > 1) return cmd[1..end];
        }
        var space = cmd.IndexOf(' ');
        return space > 0 ? cmd[..space] : cmd;
    }

    private static bool LooksWindowsPath(string raw)
    {
        try
        {
            var s = Environment.ExpandEnvironmentVariables(raw).ToLowerInvariant();
            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();
            if (winDir.Length > 0 && s.StartsWith(winDir + "\\")) return true;
            return s.Contains(@"\windows\system32\") || s.Contains(@"\windows\syswow64\");
        }
        catch { return false; }
    }
}
