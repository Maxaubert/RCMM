using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
        ShellexNameIndex shellexIndex)
    {
        _capture = capture;
        _targets = targets;
        _mapper = mapper;
        _hideService = hideService;
        _reg = reg;
        _files = files;
        _shellexIndex = shellexIndex;
    }

    public bool RequiresExplorerRestart
        => _pendingHide.Values.Concat(_pendingUnhide.Values)
                       .SelectMany(t => t)
                       .Any(t => t.Kind == HideKind.HkcuMask);

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
        var captures = _capture.CaptureAll(_targets.GetTargets());
        var merged = MergeCaptures(captures);
        var nameIndex = _shellexIndex.BuildNameToClsidMap();

        _allRows.Clear();
        foreach (var item in merged)
        {
            var effectiveItem = item;
            if (string.IsNullOrEmpty(effectiveItem.Verb) && string.IsNullOrEmpty(effectiveItem.OwnerClsid))
            {
                if (nameIndex.TryGetValue(effectiveItem.DisplayName, out var clsid))
                    effectiveItem = effectiveItem with { OwnerClsid = clsid };
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
        }

        FilterIntoAllEntries();
        _pendingHide.Clear();
        _pendingUnhide.Clear();
        PendingChangeIds.Clear();
        Raise(nameof(RequiresExplorerRestart));
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
            result.AddRange(_mapper.MapVerb(item.Verb!));
        if (!string.IsNullOrEmpty(item.OwnerClsid))
            result.AddRange(_mapper.MapClsid(item.OwnerClsid!));
        return result;
    }

    private string? ResolveIconPath(CapturedItem item, IReadOnlyList<HideTarget> targets)
    {
        foreach (var target in targets)
        {
            if (target.Kind != HideKind.LegacyDisable) continue;
            var icon = _reg.GetValue(target.Hive, target.Path, "Icon") as string;
            if (!string.IsNullOrWhiteSpace(icon)) return icon;
            var cmd = _reg.GetValue(target.Hive, target.Path + @"\command", "") as string;
            if (!string.IsNullOrWhiteSpace(cmd)) return cmd;
        }
        return null;
    }

    private bool AllTargetsHidden(IReadOnlyList<HideTarget> targets)
    {
        if (targets.Count == 0) return false;
        foreach (var t in targets)
        {
            if (t.Kind == HideKind.LegacyDisable)
            {
                if (_reg.GetValue(t.Hive, t.Path, t.ValueName ?? "LegacyDisable") == null) return false;
            }
            else
            {
                if (!_reg.KeyExists(t.Hive, t.Path)) return false;
            }
        }
        return true;
    }

    private void OnRowToggled(EntryRowViewModel row, bool isHidden)
    {
        var id = row.Entry.Id;
        if (isHidden == AllTargetsHidden(row.Entry.HideTargets))
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
        foreach (var (_, targets) in _pendingHide) _hideService.Hide(targets);
        foreach (var (_, targets) in _pendingUnhide) _hideService.Unhide(targets);
        _pendingHide.Clear();
        _pendingUnhide.Clear();
        PendingChangeIds.Clear();
        Raise(nameof(RequiresExplorerRestart));
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
        if (string.IsNullOrEmpty(probe)) return (null, false);

        var info = _files.Read(probe);
        var company = info.CompanyName;
        bool builtIn = (company != null && company.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0)
                        || LooksWindowsPath(probe);
        return (company, builtIn);
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
