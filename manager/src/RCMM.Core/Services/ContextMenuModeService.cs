namespace RCMM.Core.Services;

/// <summary>
/// Switches Windows 11 between its modern context menu and the classic
/// ("Show more options") menu via the well-known per-user "legacy menu" hack:
/// when <c>HKCU\Software\Classes\CLSID\{86ca1aa0-…}\InprocServer32</c> exists with
/// an empty default value, Explorer serves the classic menu by default; removing
/// the key restores the Win11 modern menu.
///
/// Only this single CLSID key is touched — the user's hide markers
/// (LegacyDisable / Blocked list) and added verbs (RCMM.* keys) are left alone, so
/// switching modes never requires re-applying Show/Hide or Add-to-menu. The change
/// takes effect after an Explorer restart. This is the same key
/// <see cref="CascadeProtectionService.LegacyMenuHackKey"/> consults.
/// </summary>
public sealed class ContextMenuModeService
{
    /// <summary>The modern-menu CLSID the hack neutralises (HKCU-relative).</summary>
    public const string ClsidKey = "Software\\Classes\\CLSID\\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    /// <summary>The empty InprocServer32 subkey whose presence forces the classic menu.</summary>
    public const string InprocKey = ClsidKey + "\\InprocServer32";

    private readonly IRegistry _reg;

    public ContextMenuModeService(IRegistry reg) { _reg = reg; }

    /// <summary>True when the classic menu is forced (the hack key is present).</summary>
    public bool IsClassic() => _reg.KeyExists(RegistryHive.CurrentUser, InprocKey);

    /// <summary>Force the classic menu (<paramref name="classic"/> = true) or restore
    /// the Win11 modern menu (false). Idempotent. Returns true if the registry
    /// actually changed (i.e. an Explorer restart is warranted).</summary>
    public bool SetClassic(bool classic)
    {
        if (classic == IsClassic()) return false;   // already in the requested mode
        if (classic)
        {
            _reg.CreateKey(RegistryHive.CurrentUser, InprocKey);
            _reg.SetValue(RegistryHive.CurrentUser, InprocKey, "", "");   // empty (default) value is the hack
        }
        else
        {
            _reg.DeleteKey(RegistryHive.CurrentUser, ClsidKey);   // drop the whole CLSID → modern menu
        }
        return true;
    }
}
