using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public sealed class HideService
{
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
            if (t.Kind == HideKind.LegacyDisable)
            {
                _reg.SetValue(t.Hive, t.Path, t.ValueName ?? "LegacyDisable", "");
            }
            else // HkcuMask
            {
                _reg.CreateKey(t.Hive, t.Path);
                _reg.SetValue(t.Hive, t.Path, "", "");
            }
        }
    }

    public void Unhide(IReadOnlyList<HideTarget> targets)
    {
        foreach (var t in targets)
        {
            if (t.Kind == HideKind.LegacyDisable)
            {
                _reg.DeleteValue(t.Hive, t.Path, t.ValueName ?? "LegacyDisable");
            }
            else // HkcuMask
            {
                _reg.DeleteKey(t.Hive, t.Path);
            }
        }
    }

    public static bool RequiresExplorerRestart(IReadOnlyList<HideTarget> targets)
    {
        foreach (var t in targets)
            if (t.Kind == HideKind.HkcuMask) return true;
        return false;
    }

    private static string MaskPath(ContextMenuEntry entry)
        => @"Software\Classes\" + entry.Scope.ToRegistryRoot()
           + @"\shellex\ContextMenuHandlers\" + entry.OriginalKeyName;
}
