using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public sealed class HideService
{
    public const string BlockedListPath =
        @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

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
                    _reg.DeleteKey(t.Hive, t.Path);
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
