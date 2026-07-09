using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public sealed class HideService
{
    public const string BlockedListPath =
        @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    /// <summary>Private value under a masked HKCU shellex key where Hide stashes the
    /// original default (the real CLSID) when the key was a genuine per-user
    /// registration rather than RCMM's empty shadow, so Unhide can restore it instead
    /// of destroying the handler. The "RCMM." prefix keeps it clearly ours.</summary>
    public const string SavedDefaultValueName = "RCMM.SavedDefault";

    /// <summary>
    /// Constructs a HideTarget that blocks a shell extension CLSID via the
    /// Shell Extensions\Blocked list. HKCU is tried first because it doesn't
    /// need elevation; if a particular packaged extension is honored only at
    /// HKLM, the caller should add a second target for LocalMachine.
    /// </summary>
    public static HideTarget BlockedShellExtTarget(string clsid, RegistryHive hive = RegistryHive.CurrentUser)
        => new HideTarget(HideKind.BlockedShellExt, hive, BlockedListPath, clsid);


    private readonly IRegistry _reg;

    public HideService(IRegistry reg) { _reg = reg; }

    public void Hide(ContextMenuEntry entry)
    {
        switch (entry.Kind)
        {
            case EntryKind.ShellVerb:
                _reg.SetValue(RegistryHive.ClassesRoot, entry.RegistryPath, "LegacyDisable", "");
                break;
            case EntryKind.ShellExtension:
                _reg.CreateKey(RegistryHive.CurrentUser, MaskPath(entry));
                _reg.SetValue(RegistryHive.CurrentUser, MaskPath(entry), "", "");
                break;
        }
    }

    public void Unhide(ContextMenuEntry entry)
    {
        switch (entry.Kind)
        {
            case EntryKind.ShellVerb:
                _reg.DeleteValue(RegistryHive.ClassesRoot, entry.RegistryPath, "LegacyDisable");
                break;
            case EntryKind.ShellExtension:
                _reg.DeleteKey(RegistryHive.CurrentUser, MaskPath(entry));
                break;
        }
    }

    public static bool RequiresExplorerRestart(EntryKind kind) => kind == EntryKind.ShellExtension;

    public void Hide(IReadOnlyList<HideTarget> targets)
    {
        foreach (var t in targets)
        {
            switch (t.Kind)
            {
                case HideKind.LegacyDisable:
                    _reg.SetValue(t.Hive, t.Path, t.ValueName ?? "LegacyDisable", "");
                    break;
                case HideKind.HkcuMask:
                    // The mask works by giving the HKCU\Software\Classes shellex key an
                    // empty default value, which wins in HKCR's merged view and stops the
                    // handler loading. That assumes the key is a throwaway shadow over an
                    // HKLM original. But a per-user-installed handler's REAL registration
                    // lives at exactly this key — emptying its default would corrupt it and
                    // a later Unhide's DeleteKey would destroy it. So if the key already
                    // holds a real CLSID, stash it first; Unhide restores from the stash
                    // instead of deleting. See the HkcuMask data-loss audit finding.
                    if (_reg.KeyExists(t.Hive, t.Path)
                        && _reg.GetValue(t.Hive, t.Path, "") is string existing
                        && !string.IsNullOrEmpty(existing)
                        && _reg.GetValue(t.Hive, t.Path, SavedDefaultValueName) == null)
                    {
                        _reg.SetValue(t.Hive, t.Path, SavedDefaultValueName, existing);
                    }
                    _reg.CreateKey(t.Hive, t.Path);
                    _reg.SetValue(t.Hive, t.Path, "", "");
                    break;
                case HideKind.BlockedShellExt:
                    // ValueName is the CLSID. Data is ignored by Explorer; conventional value is "".
                    _reg.CreateKey(t.Hive, t.Path);
                    _reg.SetValue(t.Hive, t.Path, t.ValueName!, "");
                    break;
            }
        }
    }

    public void Unhide(IReadOnlyList<HideTarget> targets)
    {
        foreach (var t in targets)
        {
            switch (t.Kind)
            {
                case HideKind.LegacyDisable:
                    _reg.DeleteValue(t.Hive, t.Path, t.ValueName ?? "LegacyDisable");
                    break;
                case HideKind.HkcuMask:
                    // Mirror of Hide. If we stashed a real per-user registration, restore
                    // it and drop the stash rather than deleting the key. If the key still
                    // holds a real (non-empty) default we never masked, leave it alone —
                    // deleting would wipe a live handler. Only delete the empty shadow keys
                    // we actually created to mask an HKLM original.
                    var saved = _reg.GetValue(t.Hive, t.Path, SavedDefaultValueName) as string;
                    if (saved != null)
                    {
                        _reg.SetValue(t.Hive, t.Path, "", saved);
                        _reg.DeleteValue(t.Hive, t.Path, SavedDefaultValueName);
                    }
                    else if (string.IsNullOrEmpty(_reg.GetValue(t.Hive, t.Path, "") as string))
                    {
                        _reg.DeleteKey(t.Hive, t.Path);
                    }
                    break;
                case HideKind.BlockedShellExt:
                    _reg.DeleteValue(t.Hive, t.Path, t.ValueName!);
                    break;
            }
        }
    }

    public static bool RequiresExplorerRestart(IReadOnlyList<HideTarget> targets)
    {
        foreach (var t in targets)
            if (t.Kind == HideKind.HkcuMask || t.Kind == HideKind.BlockedShellExt) return true;
        return false;
    }

    private static string MaskPath(ContextMenuEntry entry)
        => @"Software\Classes\" + entry.Scope.ToRegistryRoot()
           + @"\shellex\ContextMenuHandlers\" + entry.OriginalKeyName;
}
