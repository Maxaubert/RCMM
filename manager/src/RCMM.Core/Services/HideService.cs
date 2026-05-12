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

    private static string MaskPath(ContextMenuEntry entry)
        => @"Software\Classes\" + entry.Scope.ToRegistryRoot()
           + @"\shellex\ContextMenuHandlers\" + entry.OriginalKeyName;
}
