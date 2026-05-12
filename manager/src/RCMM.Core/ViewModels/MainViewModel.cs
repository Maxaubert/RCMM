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
            int pos = 0;
            int packagedAdded = 0;
            foreach (var pkg in _packagedScanner.Scan())
            {
                _packagedClsids.Add(pkg.Clsid);
                if (!_packagedPublishers.ContainsKey(pkg.Clsid))
                    _packagedPublishers[pkg.Clsid] = pkg.PublisherDisplayName;
                if (pkg.DllPath != null && !_packagedDllByClsid.ContainsKey(pkg.Clsid))
                    _packagedDllByClsid[pkg.Clsid] = pkg.DllPath;
                allItems.Add(new CapturedItem
                {
                    TargetPath = $"<packaged:{pkg.PackageFullName}>",
                    Position = pos++,
                    DisplayName = pkg.DisplayName,
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
        var merged = MergeCaptures(allItems).ToList();
        Log.Info("rescan", $"captured={allItems.Count} mergedUnique={merged.Count}");
        var nameIndex = _shellexIndex.BuildNameToClsidMap();
        var wordIndex = _shellexIndex.BuildClsidWordIndex();
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
            var effectiveItem = item;
            if (string.IsNullOrEmpty(effectiveItem.OwnerClsid))
            {
                if (nameIndex.TryGetValue(effectiveItem.DisplayName, out var clsid))
                    effectiveItem = effectiveItem with { OwnerClsid = clsid };
                else
                {
                    // Fuzzy: link "Restore previous versions" to "Previous Versions Property Page",
                    // "Uninstall with Revo Uninstaller Pro" to "Revo Uninstaller Pro Extension", etc.
                    var fuzzy = _shellexIndex.FuzzyMatch(effectiveItem.DisplayName, wordIndex);
                    if (fuzzy != null)
                        effectiveItem = effectiveItem with { OwnerClsid = fuzzy };
                    else if (invokerMap != null && invokerMap.TryGetValue(effectiveItem.DisplayName, out var invokerClsid))
                        effectiveItem = effectiveItem with { OwnerClsid = invokerClsid };
                }
            }

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
            AllEntries.Add(row);
        }
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
            // expose the handler's CLSID through one of these fields. Resolve to the
            // handler DLL's first icon as a fallback when no Icon / command is given.
            foreach (var field in new[] { "ExplorerCommandHandler", "VerbHandler", "CommandStateHandler", "CanonicalName" })
            {
                if (_reg.GetValue(RegistryHive.ClassesRoot, hkcrPath, field) is string handlerClsid
                    && LooksLikeClsid(handlerClsid))
                {
                    var dll = _reg.GetValue(RegistryHive.ClassesRoot, $@"CLSID\{handlerClsid}\InprocServer32", "") as string;
                    if (!string.IsNullOrWhiteSpace(dll)) return dll;
                }
            }
            if (_reg.GetValue(RegistryHive.ClassesRoot, hkcrPath + @"\command", "DelegateExecute") is string delegateClsid
                && LooksLikeClsid(delegateClsid))
            {
                var dll = _reg.GetValue(RegistryHive.ClassesRoot, $@"CLSID\{delegateClsid}\InprocServer32", "") as string;
                if (!string.IsNullOrWhiteSpace(dll)) return dll;
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
        // icon resource of the registered DLL.
        if (!string.IsNullOrEmpty(item.OwnerClsid))
        {
            var clsidPath = $@"CLSID\{item.OwnerClsid}";
            var defaultIcon = _reg.GetValue(RegistryHive.ClassesRoot, clsidPath + @"\DefaultIcon", "") as string;
            if (!string.IsNullOrWhiteSpace(defaultIcon)) return defaultIcon;
            var dll = _reg.GetValue(RegistryHive.ClassesRoot, clsidPath + @"\InprocServer32", "") as string;
            if (!string.IsNullOrWhiteSpace(dll)) return dll;

            // Packaged COM extensions aren't in classic HKCR\CLSID; their DLL lives
            // under C:\Program Files\WindowsApps\<package>\… resolved from the AppX
            // store and PackagedCom\Package\<pkg>\Class\<CLSID>\DllPath.
            if (_packagedDllByClsid.TryGetValue(item.OwnerClsid!, out var packagedDll))
                return packagedDll;
        }

        return null;
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
