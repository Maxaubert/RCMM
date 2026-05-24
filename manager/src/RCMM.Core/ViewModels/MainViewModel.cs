using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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

    // Posts an action to the UI thread. Defaults to inline execution so tests
    // and headless callers run synchronously; the app injects a DispatcherQueue
    // marshaller. All AllEntries / PendingChangeIds / PropertyChanged mutations
    // go through this so they never touch a bound collection off the UI thread.
    private readonly Action<Action> _post;

    private readonly HashSet<string> _packagedClsids =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _packagedPublishers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _packagedDllByClsid =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _packagedLogoByClsid =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _observedClsids =
        new(StringComparer.OrdinalIgnoreCase);
    // CLSIDs whose technical DisplayName the rename pass successfully replaced
    // with a friendly one. Treated like "observed" by FilterIntoAllEntries so
    // the unrelated unobserved-CLSID filter doesn't immediately re-hide the
    // row we just made readable (Modern Share, Defender Scan, NVIDIA, ...).
    private readonly HashSet<string> _renamedClsids =
        new(StringComparer.OrdinalIgnoreCase);

    // Last-resort DisplayName overrides for shellex handlers whose menu text
    // we can't recover from the registry or COM probes — typically because
    // the handler refuses to populate IContextMenu without admin/admin-elevated
    // context, and it doesn't implement IExplorerCommand. Keyed by the
    // FileDescription that ClassicShellexScanner picks up.
    private static readonly Dictionary<string, string> _technicalDisplayNameOverrides
        = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft Security Client Shell Extension"] = "Scan with Microsoft Defender",
        ["NVIDIA Display Shell Extension"] = "NVIDIA Control Panel",
    };

    // CLSID → icon-path overrides for shellexes whose registered DLL is
    // empty of icon resources (Windows renders the menu icon via an HBITMAP
    // the handler emits at runtime, which we can't intercept here). The
    // override points at a sibling DLL/exe with the same publisher that does
    // contain the right icon. Keyed by CLSID — CLSIDs are case-insensitive
    // in the registry so the comparer is OrdinalIgnoreCase.
    private static readonly Dictionary<string, string> _iconPathOverridesByClsid
        = new(StringComparer.OrdinalIgnoreCase)
    {
        // NVIDIA's NvCplDesktopContext shellex DLL (nvshext.dll in
        // System32\DriverStore) has zero icon resources; nvcpl.dll alongside
        // it carries the NVIDIA Control Panel icon at index 0.
        ["{3D1975AF-48C6-4f8e-A182-BE0E08FA86A9}"] = @"%SystemRoot%\System32\nvcpl.dll",
    };


    private readonly Dictionary<string, IReadOnlyList<HideTarget>> _pendingHide = new();
    private readonly Dictionary<string, IReadOnlyList<HideTarget>> _pendingUnhide = new();
    private readonly List<EntryRowViewModel> _allRows = new();
    private bool _showBuiltIns = true;

    public ObservableCollection<EntryRowViewModel> AllEntries { get; } = new();
    public ObservableCollection<string> PendingChangeIds { get; } = new();

    /// <summary>Raised after Rescan finishes populating AllEntries — host uses
    /// this to (re-)load icons for the new rows.</summary>
    public event Action? RescanComplete;

    private readonly AddPageViewModel? _addPage;
    private readonly AdditionApplier? _additionApplier;
    private readonly KnownEntriesStore _knownStore = new(KnownEntriesStore.DefaultPath());

    /// <summary>View-model backing the Add page (templates / custom entries / folders).</summary>
    public AddPageViewModel? AddPage => _addPage;

    private readonly CascadeProtectionService? _cascadeProtector;
    // Background-scoped packaged-COM extensions, keyed by CLSID. Populated by
    // Rescan from the PackagedShellExtScanner output. ApplyPending consults
    // this to decide whether to install cascade-protection verbs before
    // touching the HKCU Shell Extensions\Blocked list.
    private readonly Dictionary<string, PackagedShellExt> _backgroundExtsByClsid =
        new(StringComparer.OrdinalIgnoreCase);

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
        ShellexInvoker? shellexInvoker = null,
        AddPageViewModel? addPage = null,
        AdditionApplier? additionApplier = null,
        CascadeProtectionService? cascadeProtector = null,
        Action<Action>? postToUi = null)
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
        _addPage = addPage;
        _additionApplier = additionApplier;
        _cascadeProtector = cascadeProtector;
        _post = postToUi ?? (a => a());
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
        try { RescanCore(); }
        catch (Exception ex) { Log.Error("rescan", "rescan failed", ex); }
    }

    /// <summary>Runs the rescan pipeline on a background thread. UI-affecting
    /// mutations are marshaled back via the injected postToUi dispatcher.
    /// Delegates to the guarded <see cref="Rescan"/>, so the returned Task never
    /// faults — RescanCore's exceptions are logged and swallowed. This is by
    /// design: the startup caller uses fire-and-forget (<c>_ = RescanAsync()</c>)
    /// and must not leave an unobserved faulted Task.</summary>
    public Task RescanAsync() => Task.Run(Rescan);

    private void RescanCore()
    {
        Log.Info("rescan", "begin");
        var targets = _targets.GetTargets();
        Log.Debug("rescan", $"targets={targets.Count}");
        var allItems = new List<CapturedItem>();
        allItems.AddRange(_capture.CaptureAll(targets));
        int liveCount = allItems.Count;

        _packagedClsids.Clear();
        _packagedPublishers.Clear();
        _packagedDllByClsid.Clear();
        _packagedLogoByClsid.Clear();
        _backgroundExtsByClsid.Clear();
        _renamedClsids.Clear();

        // Defer iteration: get the packaged + registry CLSIDs first so they can be
        // registered alongside CommandStore CLSIDs in a single upfront invoker
        // probe. ShellexInvoker.BuildDisplayNameToClsidMap caches per-process —
        // anything not registered before that first call is invisible to the
        // rename pass and stays labelled by its DLL FileDescription.
        var pkgList = _packagedScanner?.Scan().ToList() ?? new List<PackagedShellExt>();
        var registryItems = _registryScanner?.ScanAsCaptures().ToList() ?? new List<CapturedItem>();

        if (_shellexInvoker != null)
        {
            foreach (var pkg in pkgList)
                _shellexInvoker.RegisterExtraClsid(pkg.Clsid);
            foreach (var item in allItems)
                if (!string.IsNullOrEmpty(item.OwnerClsid))
                    _shellexInvoker.RegisterExtraClsid(item.OwnerClsid!);
            foreach (var item in registryItems)
                if (!string.IsNullOrEmpty(item.OwnerClsid))
                    _shellexInvoker.RegisterExtraClsid(item.OwnerClsid!);
            // CommandStore handler CLSIDs (Windows.ModernShare, Windows.CompressTo,
            // ...). On Windows 11 these modern verbs aren't returned by legacy
            // IContextMenu, so the only way to learn their friendly menu text is
            // to ask IExplorerCommand::GetTitle directly — that requires them to
            // be in the invoker's probe set BEFORE BuildDisplayNameToClsidMap.
            if (_commandStore != null)
                foreach (var clsid in _commandStore.AllClsids())
                    _shellexInvoker.RegisterExtraClsid(clsid);
            // Also include verb-handler CLSIDs reachable from captured verbs'
            // HKCR registrations (ExplorerCommandHandler / VerbHandler fields).
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
            _shellexInvoker.BuildDisplayNameToClsidMap();
        }

        // Packaged COM context menus go BEFORE the classic registry scan because
        // (a) their friendly DisplayName is more accurate than a DLL's FileDescription
        // and (b) they own the BlockedShellExt hide-target path; ResolveHideTargets
        // consults _packagedClsids to pick the right hide path.
        int pos = 0;
        int packagedAdded = 0;
        foreach (var pkg in pkgList)
        {
            _packagedClsids.Add(pkg.Clsid);
            if (!_packagedPublishers.ContainsKey(pkg.Clsid))
                _packagedPublishers[pkg.Clsid] = pkg.PublisherDisplayName;
            if (pkg.DllPath != null && !_packagedDllByClsid.ContainsKey(pkg.Clsid))
                _packagedDllByClsid[pkg.Clsid] = pkg.DllPath;
            if (pkg.LogoPath != null && !_packagedLogoByClsid.ContainsKey(pkg.Clsid))
                _packagedLogoByClsid[pkg.Clsid] = pkg.LogoPath;
            if (pkg.IsBackgroundExtension)
                _backgroundExtsByClsid[pkg.Clsid] = pkg;

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

        if (registryItems.Count > 0)
        {
            allItems.AddRange(registryItems);
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
                // Fallback chain — first one with a non-technical, non-empty title wins.
                //   (1) IShellExtInit+IContextMenu probe results ("Scan for deleted files"
                //       for Recuva's CLSID once it emitted that string during probing).
                //   (2) IExplorerCommand::GetTitle — Modern Share's CLSID returns "Share"
                //       here even though the registry only has the COM ProgID
                //       "Ribbon Modern Share Verb".
                //   (3) CommandStore verb-name derivation — covers any OS modern verb
                //       whose handler is registered in CommandStore but doesn't
                //       implement IExplorerCommand.
                //   (4) Static override table for well-known third-party handlers
                //       (Defender, NVIDIA) that don't probe and have no CommandStore
                //       entry; their menu text is well-known and stable enough to
                //       hardcode rather than leave the row hidden.
                string? renamed = _shellexInvoker?.LookupEmittedNames(item.OwnerClsid)
                    .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n) && !LooksTechnical(n));
                if (string.IsNullOrWhiteSpace(renamed))
                {
                    var title = _shellexInvoker?.LookupTitle(item.OwnerClsid);
                    if (!string.IsNullOrWhiteSpace(title) && !LooksTechnical(title!))
                        renamed = title;
                }
                if (string.IsNullOrWhiteSpace(renamed))
                {
                    var cs = _commandStore?.FriendlyTitleForClsid(item.OwnerClsid);
                    if (!string.IsNullOrWhiteSpace(cs) && !LooksTechnical(cs!))
                        renamed = cs;
                }
                if (string.IsNullOrWhiteSpace(renamed)
                    && _technicalDisplayNameOverrides.TryGetValue(item.DisplayName, out var hardcoded))
                    renamed = hardcoded;

                if (!string.IsNullOrWhiteSpace(renamed))
                {
                    item = item with { DisplayName = renamed! };
                    _renamedClsids.Add(item.OwnerClsid!);
                }
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

        // Every handler CLSID we care about was already registered with the
        // invoker in the upfront pass, so a second BuildDisplayNameToClsidMap
        // call returns the cached map. We still log its size for diagnostics.
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
            var (source, isBuiltIn) = ResolveSourceAndBuiltIn(effectiveItem, hideTargets, iconPath);
            // "Create shortcut" ships in shell32 but its hide-target classification
            // doesn't always resolve to a Windows DLL probe (the verb's CLSID handler
            // may live elsewhere), so force it into the Windows-specific bucket.
            if (string.Equals(effectiveItem.DisplayName, "Create shortcut", StringComparison.OrdinalIgnoreCase))
                isBuiltIn = true;
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

        // Restore ghost entries: any row we saw on a previous rescan that's
        // no longer present in _allRows, but whose hide targets are currently
        // set in the registry, gets injected back as a hidden row. This is the
        // only recovery path for CommandStore-only verbs like Windows.Share
        // ("Give access to") — they have no HKCR\<scope>\shell key the
        // registry scanners can find, and the live IContextMenu probe stops
        // returning them once the user hides them. Without this restore, the
        // entry vanishes from RCMM and the user can't un-toggle it.
        RestoreGhostEntries();

        _pendingHide.Clear();
        _pendingUnhide.Clear();
        // Persist the current snapshot so the next rescan can recover ghosts.
        _knownStore.Save(_allRows.ConvertAll(r => r.Entry));
        for (int i = 0; i < _allRows.Count; i++)
        {
            var r = _allRows[i];
            var src = string.IsNullOrEmpty(r.Entry.Source) ? "Unknown" : r.Entry.Source;
            Log.Debug("dump", $"#{i:D2} '{r.Entry.DisplayName}' src='{src}' sub={r.Entry.IsSubmenu} hideTargets={r.Entry.HideTargets.Count} icon='{r.Entry.IconPath ?? ""}'");
        }
        // UI tail: FilterIntoAllEntries (mutates AllEntries), PendingChangeIds.Clear,
        // and the PropertyChanged raises are all bound-collection / UI-thread ops, so
        // they run together as the single UI-thread hand-off for a completed rescan.
        _post(() =>
        {
            FilterIntoAllEntries();
            PendingChangeIds.Clear();
            Raise(nameof(RequiresExplorerRestart));
            RescanComplete?.Invoke();
            Log.Info("rescan", $"end rows={_allRows.Count} withHideTargets={rowsWithHide} builtIn={rowsBuiltIn} visible={AllEntries.Count}");
        });
    }

    private static readonly string[] _suppressedDisplayNames =
    {
        "File ownership",
        "Launches Sync Center",
        "Launches Sync Center.",
        "Work Folders",
        "App Resolver",
    };

    private void FilterIntoAllEntries()
    {
        AllEntries.Clear();
        foreach (var row in _allRows)
        {
            if (row.IsBuiltIn && !_showBuiltIns) continue;
            // Conditional / informational handlers that surface during probing but
            // aren't real menu options the user can act on.
            if (_suppressedDisplayNames.Contains(row.Entry.DisplayName, StringComparer.OrdinalIgnoreCase)) continue;
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
                // A CLSID counts as "real" if any of: it's a packaged COM ext;
                // we observed it on a live capture; we renamed it from a
                // technical FileDescription (Share, Defender, NVIDIA);
                // CommandStore lists it as a handler for a Windows OS verb
                // (modern verbs that legacy IContextMenu doesn't return); or
                // it is *currently hidden* — without this last clause, a
                // user-hidden shellex vanishes from the next rescan because
                // Explorer no longer surfaces it via IContextMenu, leaving
                // no way to un-hide it from RCMM's UI.
                if (!_packagedClsids.Contains(clsid)
                    && !_observedClsids.Contains(clsid)
                    && !_renamedClsids.Contains(clsid)
                    && !(_commandStore?.IsKnownClsid(clsid) ?? false)
                    && !row.Entry.IsHidden)
                    continue;
            }
            AllEntries.Add(row);
        }
    }

    /// <summary>
    /// Walks <see cref="_allRows"/>, groups by case-insensitive DisplayName, and
    /// collapses each group into a single row. The winner is the row most likely
    /// <summary>
    /// Inject "ghost" rows: entries we persisted from a previous rescan that
    /// aren't present in the current _allRows but whose hide targets are still
    /// set in the registry. Without this pass, CommandStore-only verbs
    /// (Windows.Share / "Give access to" and similar) disappear from RCMM
    /// after the user hides them, leaving no way to un-toggle them.
    ///
    /// A ghost is added only if AllTargetsHidden reports true for its stored
    /// hide targets — so entries the user has since unhidden, or entries that
    /// were uninstalled and have no hide markers left, are not resurrected.
    /// </summary>
    private void RestoreGhostEntries()
    {
        var present = new HashSet<string>(_allRows.Select(r => r.Entry.Id), StringComparer.OrdinalIgnoreCase);
        // Track which BlockedShellExt CLSIDs are already represented so a
        // persisted entry doesn't duplicate an existing CLSID-based row.
        var coveredClsids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _allRows)
            foreach (var t in r.Entry.HideTargets)
                if (t.Kind == HideKind.BlockedShellExt && !string.IsNullOrEmpty(t.ValueName))
                    coveredClsids.Add(t.ValueName);

        int restored = 0;
        foreach (var prev in _knownStore.Load())
        {
            if (present.Contains(prev.Id)) continue;
            if (prev.HideTargets.Count == 0) continue;
            if (!AllTargetsHidden(prev.HideTargets)) continue;
            // Skip if every CLSID this entry would block is already covered
            // — the existing row's toggle already manages the same handlers,
            // so a duplicate ghost would just clutter the list.
            var prevBlockClsids = prev.HideTargets
                .Where(t => t.Kind == HideKind.BlockedShellExt && !string.IsNullOrEmpty(t.ValueName))
                .Select(t => t.ValueName!)
                .ToList();
            if (prevBlockClsids.Count > 0 && prevBlockClsids.All(coveredClsids.Contains)) continue;
            // Build a fresh MenuEntry with IsHidden recomputed and IconBytes
            // cleared (the icon loader fetches them again from IconPath).
            var ghost = prev with { IsHidden = true, IconBytes = null };
            var row = new EntryRowViewModel(ghost) { HiddenChanged = OnRowToggled };
            _allRows.Add(row);
            present.Add(prev.Id);
            foreach (var c in prevBlockClsids) coveredClsids.Add(c);
            restored++;
        }

        // Second pass: walk CommandStore verbs and surface any whose handler
        // CLSIDs are all currently in the BlockedShellExt list AND aren't
        // already covered by an existing row. This recovers verbs the user
        // hid before KnownEntriesStore was populated (e.g. before this fix
        // shipped) — Windows.Share / "Give access to" is the canonical case.
        // Without this they'd be permanently invisible until the user
        // manually clears the registry block.
        if (_commandStore != null)
        {
            // coveredClsids is already maintained by the first pass above
            // (Mechanism A's loop adds each restored entry's CLSIDs into it),
            // so candidate verbs whose CLSIDs are already represented by an
            // existing row are naturally filtered out below.
            int cmdStoreRestored = 0;
            foreach (var verbKey in _commandStore.AllVerbKeys())
            {
                var verbId = "verb:" + verbKey.ToLowerInvariant();
                if (present.Contains(verbId)) continue;

                var clsids = _commandStore.LookupClsids(verbKey).ToList();
                if (clsids.Count == 0) continue;
                if (clsids.Any(c => coveredClsids.Contains(c))) continue;

                var hideTargets = clsids.Select(c => HideService.BlockedShellExtTarget(c)).ToList();
                if (!AllTargetsHidden(hideTargets)) continue;

                var display = _wellKnownVerbDisplayNames.TryGetValue(verbKey, out var hardcoded)
                    ? hardcoded
                    : (_commandStore.FriendlyTitleForClsid(clsids[0]) ?? verbKey);
                var entry = new MenuEntry
                {
                    Id = verbId,
                    DisplayName = display,
                    Source = "Microsoft Corporation",
                    IsBuiltIn = true,
                    IsHidden = true,
                    IsSubmenu = false,
                    HideTargets = hideTargets,
                };
                _allRows.Add(new EntryRowViewModel(entry) { HiddenChanged = OnRowToggled });
                present.Add(verbId);
                foreach (var c in clsids) coveredClsids.Add(c);
                cmdStoreRestored++;
            }
            if (cmdStoreRestored > 0)
                Log.Info("rescan", $"restored commandStoreBlockedVerbs={cmdStoreRestored}");
        }

        if (restored > 0) Log.Info("rescan", $"restored ghostEntries={restored}");
    }

    /// <summary>
    /// Hardcoded display names for CommandStore verbs whose menu text is
    /// localised by Windows and not derivable from the verb key. Keeps the
    /// recovered ghost entry's label close to what the user actually saw in
    /// their right-click menu before they hid it.
    /// </summary>
    private static readonly Dictionary<string, string> _wellKnownVerbDisplayNames
        = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Windows.Share"] = "Give access to",
    };

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
        // 0a. Hardcoded CLSID → icon-path override for shellexes whose
        // registered DLL has no icon resources (NVIDIA's nvshext.dll, ...).
        // These handlers emit the menu icon via HBITMAP at runtime, which we
        // can't intercept here; the override points at a sibling binary
        // shipped by the same publisher that does carry the icon.
        if (!string.IsNullOrEmpty(item.OwnerClsid)
            && _iconPathOverridesByClsid.TryGetValue(item.OwnerClsid, out var iconOverride))
        {
            var expanded = Environment.ExpandEnvironmentVariables(iconOverride);
            if (System.IO.File.Exists(expanded)) return expanded;
        }

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
                    if (!IsGenericIconLibrary(dll) && !string.IsNullOrWhiteSpace(dll)) return dll;
                }
            }
            if (_reg.GetValue(RegistryHive.ClassesRoot, hkcrPath + @"\command", "DelegateExecute") is string delegateClsid
                && LooksLikeClsid(delegateClsid))
            {
                var dll = _reg.GetValue(RegistryHive.ClassesRoot, $@"CLSID\{delegateClsid}\InprocServer32", "") as string;
                if (!IsGenericIconLibrary(dll) && !string.IsNullOrWhiteSpace(dll)) return dll;
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
            if (!string.IsNullOrWhiteSpace(dll) && !IsGenericIconLibrary(dll)) return dll;

            // Packaged COM extensions aren't in classic HKCR\CLSID; their DLL lives
            // under C:\Program Files\WindowsApps\<package>\… resolved from the AppX
            // store and PackagedCom\Package\<pkg>\Class\<CLSID>\DllPath.
            //
            // Prefer the AppxManifest-declared logo PNG over the DLL for packaged
            // shellexes: the registered DLL often has zero icon resources because
            // the publisher expects the package's Square44x44Logo asset to be
            // rendered, and PNG assets are usually readable even when the rest of
            // the package folder isn't enumerable for non-admin users (PowerToys
            // ImageResizer, Notepad++ Microsoft Store build, etc.).
            if (_packagedLogoByClsid.TryGetValue(item.OwnerClsid!, out var packagedLogo))
                return packagedLogo;
            if (_packagedDllByClsid.TryGetValue(item.OwnerClsid!, out var packagedDll))
                return packagedDll;
        }

        // 4. Registry-scanner pre-resolved hint. ClassicShellexScanner already
        // walks CLSID\<clsid>\InprocServer32 to read FileDescription; the same
        // path is the cheapest valid icon source when steps 0-3 returned nothing
        // (e.g. the scanner used a resolution route — Shell key, alternate
        // server registration — that the HKCR\CLSID lookup at step 3 misses).
        if (!string.IsNullOrWhiteSpace(item.IconHint) && !IsGenericIconLibrary(item.IconHint))
            return item.IconHint;

        return null;
    }

    /// <summary>
    /// True for the handful of generic-icon Windows libraries whose index-0
    /// resource is a placeholder (the shell uses an HBITMAP the handler emits
    /// at runtime, not a resource we can read here). Used as a last-mile filter
    /// in ResolveIconPath so we don't return a generic key/folder/document icon.
    ///
    /// Match is by *filename* not "path is anywhere under System32" — that
    /// earlier rule misfiled third-party DLLs like NVIDIA's
    /// System32\DriverStore\...\nvshext.dll as generic and stripped a perfectly
    /// good icon source, leaving "NVIDIA Control Panel" with no icon in the UI.
    /// </summary>
    private static bool IsGenericIconLibrary(string? path)
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
            if (comma > 0 && comma > s.LastIndexOf('\\') && comma > s.LastIndexOf('/'))
                s = s[..comma];
            var fileName = System.IO.Path.GetFileName(s.Trim());
            return fileName is "shell32.dll" or "imageres.dll" or "windows.storage.dll"
                            or "ntshrui.dll" or "ddores.dll" or "explorer.exe";
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

        // Cascade-protection pass — runs BEFORE any HKCU\…\Shell Extensions\Blocked
        // write so the protective classic verbs exist by the time Windows decides
        // whether to silently de-list other Directory\Background packaged-COM
        // extensions. We only act when (a) the protection service is wired AND
        // (b) the current hide batch actually adds a Background-scoped packaged
        // CLSID to Blocked. See CLAUDE.md "packaged-COM Directory\Background
        // cascade" for the underlying Windows behaviour we're guarding against.
        if (_cascadeProtector != null && _backgroundExtsByClsid.Count > 0)
        {
            foreach (var targets in _pendingHide.Values)
            {
                foreach (var t in targets)
                {
                    if (t.Kind != HideKind.BlockedShellExt) continue;
                    if (string.IsNullOrEmpty(t.ValueName)) continue;
                    if (!_backgroundExtsByClsid.ContainsKey(t.ValueName!)) continue;
                    var plans = _cascadeProtector.PlanProtections(t.ValueName!, _backgroundExtsByClsid.Values);
                    if (plans.Count > 0)
                    {
                        Log.Info("apply", $"cascade-protection: installing {plans.Count} classic-verb fallbacks for hide of {t.ValueName}");
                        try { _cascadeProtector.Install(plans); }
                        catch (Exception ex) { Log.Error("apply", $"cascade-protection install failed for {t.ValueName}", ex); }
                    }
                }
            }
        }

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

        // Post-unhide cleanup: when no Background-scoped packaged CLSID remains in
        // HKCU Blocked, the cascade can't trigger, so we can sweep the protective
        // verbs back out. The sweep is namespace-scoped (RcmmProtect_ prefix only)
        // so user-authored classic verbs are untouched.
        if (_cascadeProtector != null && _pendingUnhide.Count > 0 && _backgroundExtsByClsid.Count > 0)
        {
            try
            {
                bool anyBgStillBlocked = _backgroundExtsByClsid.Keys.Any(clsid =>
                    _reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, clsid) != null);
                if (!anyBgStillBlocked)
                {
                    var removed = _cascadeProtector.UninstallAll();
                    if (removed > 0)
                        Log.Info("apply", $"cascade-protection: removed {removed} protection verbs (no Background ext remains blocked)");
                }
            }
            catch (Exception ex) { Log.Error("apply", "cascade-protection sweep failed", ex); }
        }

        // Always purge any Directory\shell\RcmmProtect_* verbs left behind by
        // earlier builds that protected the folder-selected scope too. Those
        // produced a duplicate "Open in Terminal" / "AMD Software" row on
        // right-click of a selected folder. Cheap and idempotent.
        if (_cascadeProtector != null)
        {
            try
            {
                var stale = _cascadeProtector.PurgeStaleDirectoryScopeProtections();
                if (stale > 0)
                    Log.Info("apply", $"cascade-protection: purged {stale} stale Directory-scope protection verbs");
            }
            catch (Exception ex) { Log.Error("apply", "cascade-protection Directory purge failed", ex); }
        }
        if (_addPage != null && _additionApplier != null && _addPage.HasPendingChanges)
        {
            Log.Info("apply", $"additions begin entries={_addPage.Entries.Count} folders={_addPage.Folders.Count}");
            var state = _addPage.Snapshot();
            try
            {
                _additionApplier.Apply(state);
                // Persist only after registry write succeeds so a failed Apply leaves the JSON on the previous state.
                new AdditionStore(AdditionStore.DefaultPath()).Save(state);
                _post(() => _addPage.MarkClean());
            }
            catch (Exception ex) { Log.Error("apply", "additions failed", ex); }
        }
        else if (_additionApplier != null && (_pendingHide.Count > 0 || _pendingUnhide.Count > 0))
        {
            // A hide/unhide batch ran with no addition edits of its own. Re-assert
            // the user's existing added entries as part of the same Apply: after a
            // large Blocked-list hide + Explorer restart, Explorer can stop rendering
            // user-added Directory\Background verbs even though their keys are intact
            // (RCMM still captures them on rescan). A fresh registry write under
            // …\shell is what forces the classic menu to rebuild — exactly the manual
            // "re-apply additions" workaround, now automatic. Idempotent purge+rewrite;
            // nothing is dirtied or persisted because the state is unchanged.
            try
            {
                var state = _addPage != null
                    ? _addPage.Snapshot()
                    : new AdditionStore(AdditionStore.DefaultPath()).Load();
                if (state.Entries.Count > 0 || state.Folders.Count > 0)
                {
                    Log.Info("apply", $"re-asserting {state.Entries.Count} added entries after hide change (menu-cache refresh)");
                    _additionApplier.Apply(state);
                }
            }
            catch (Exception ex) { Log.Error("apply", "additions re-assert failed", ex); }
        }
        _pendingHide.Clear();
        _pendingUnhide.Clear();
        _post(() =>
        {
            PendingChangeIds.Clear();
            Raise(nameof(RequiresExplorerRestart));
        });
        Log.Info("apply", "end");
    }

    private (string? source, bool isBuiltIn) ResolveSourceAndBuiltIn(CapturedItem item, IReadOnlyList<HideTarget> targets, string? iconPath)
    {
        // Walk every kind of hide target and collect candidate "probe" paths —
        // either the verb's command-line exe (LegacyDisable) or a handler CLSID's
        // InprocServer32 DLL (BlockedShellExt + HkcuMask). For Cut / Copy / Paste
        // / Delete / Rename / Properties etc. the hide-target is BlockedShellExt
        // pointing at a CommandStore handler CLSID, so probing the CLSID's DLL is
        // the only way to discover the source — earlier code only checked
        // LegacyDisable, which is why these all came back source=Unknown,
        // IsBuiltIn=false even though their handler is shell32.dll.
        var probes = new List<string>();
        if (!string.IsNullOrEmpty(item.OwnerClsid))
        {
            var dll = _reg.GetValue(RegistryHive.ClassesRoot,
                $@"CLSID\{item.OwnerClsid}\InprocServer32", "") as string;
            if (!string.IsNullOrEmpty(dll)) probes.Add(dll);
        }
        // The resolved icon path is another strong hint at the handler binary —
        // for verbs whose CommandStore CanonicalName is a virtual id with no
        // HKCR\CLSID registration (Cut, Copy, Delete, Rename), the Icon value
        // like "shell32.dll,-16762" is the only signal that the handler lives
        // in shell32.dll.
        if (!string.IsNullOrEmpty(iconPath)) probes.Add(iconPath);
        foreach (var t in targets)
        {
            switch (t.Kind)
            {
                case HideKind.LegacyDisable:
                    var hkcrPath = HkcrPathFor(t) ?? t.Path;
                    var cmd = _reg.GetValue(RegistryHive.ClassesRoot, hkcrPath + @"\command", "") as string;
                    if (!string.IsNullOrWhiteSpace(cmd))
                    {
                        var exe = ExtractExe(cmd);
                        if (!string.IsNullOrEmpty(exe)) probes.Add(exe);
                    }
                    foreach (var field in new[] { "ExplorerCommandHandler", "VerbHandler", "CommandStateHandler" })
                    {
                        if (_reg.GetValue(RegistryHive.ClassesRoot, hkcrPath, field) is string handlerClsid
                            && LooksLikeClsid(handlerClsid))
                        {
                            var dll = _reg.GetValue(RegistryHive.ClassesRoot,
                                $@"CLSID\{handlerClsid}\InprocServer32", "") as string;
                            if (!string.IsNullOrEmpty(dll)) probes.Add(dll);
                        }
                    }
                    break;
                case HideKind.BlockedShellExt:
                    if (LooksLikeClsid(t.ValueName))
                    {
                        var dll = _reg.GetValue(RegistryHive.ClassesRoot,
                            $@"CLSID\{t.ValueName}\InprocServer32", "") as string;
                        if (!string.IsNullOrEmpty(dll)) probes.Add(dll);
                    }
                    break;
                case HideKind.HkcuMask:
                    // path = Software\Classes\<scope>\shellex\ContextMenuHandlers\<name>
                    var classes = "Software\\Classes\\";
                    if (t.Path.StartsWith(classes, StringComparison.OrdinalIgnoreCase))
                    {
                        var hkcrShellex = t.Path.Substring(classes.Length);
                        var clsidStr = _reg.GetValue(RegistryHive.ClassesRoot, hkcrShellex, "") as string;
                        if (clsidStr != null && LooksLikeClsid(clsidStr))
                        {
                            var dll = _reg.GetValue(RegistryHive.ClassesRoot,
                                $@"CLSID\{clsidStr}\InprocServer32", "") as string;
                            if (!string.IsNullOrEmpty(dll)) probes.Add(dll);
                        }
                    }
                    break;
            }
        }

        foreach (var probe in probes)
        {
            var info = _files.Read(probe);
            var company = info.CompanyName;
            if (!string.IsNullOrEmpty(company) || LooksWindowsPath(probe))
            {
                // IsBuiltIn means "Windows component, not a third-party app". DLL in
                // a Windows folder is the necessary signal but not sufficient: NVIDIA,
                // AMD, and other vendors install shellex DLLs into System32
                // (nvshext.dll, ...). A bare-name probe then resolves to a Windows
                // path even though the publisher is a third party — flagging
                // "NVIDIA Control Panel" as built-in even though it's an app.
                // Require BOTH "in Windows folder" AND "no third-party CompanyName"
                // to flag the row as built-in. Conversely, "Microsoft app in
                // Program Files / WindowsApps" (VS Code, Clipchamp, PowerToys)
                // still stays third-party because they live outside Windows folder.
                bool inWindowsFolder = LooksWindowsPath(probe);
                bool publishedByMicrosoft = string.IsNullOrEmpty(company)
                    || company!.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;
                return (company, inWindowsFolder && publishedByMicrosoft);
            }
        }

        // Packaged extensions don't have a classic CLSID InprocServer entry. Use the
        // publisher name straight from the AppX registration. Microsoft-published
        // packaged apps stay Application-specific because they aren't part of the OS.
        if (item.OwnerClsid != null && _packagedPublishers.TryGetValue(item.OwnerClsid, out var pub))
            return (pub, false);
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
            // Strip optional trailing icon index ",-NNNN" and surrounding quotes.
            if (s.StartsWith('"')) { var e = s.IndexOf('"', 1); if (e > 1) s = s[1..e]; }
            var comma = s.LastIndexOf(',');
            if (comma > 0 && comma > s.LastIndexOf('\\') && comma > s.LastIndexOf('/'))
                s = s[..comma].Trim();

            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();
            if (winDir.Length > 0 && s.StartsWith(winDir + "\\")) return true;
            if (s.Contains(@"\windows\system32\") || s.Contains(@"\windows\syswow64\")) return true;
            // Bare filenames like "shell32.dll" or "imageres.dll" resolve to System32
            // via Windows' DLL search path. Treat them as Windows binaries.
            if (!s.Contains('\\') && !s.Contains('/')
                && (s.EndsWith(".dll") || s.EndsWith(".exe") || s.EndsWith(".mui")))
            {
                var sys32 = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), s);
                if (System.IO.File.Exists(sys32)) return true;
            }
            return false;
        }
        catch { return false; }
    }
}
