namespace RCMM.Core.Models;

public enum HideKind
{
    /// <summary>Classic verb hidden by adding a "LegacyDisable" REG_SZ to its shell\verb key.</summary>
    LegacyDisable,
    /// <summary>Classic shellex CLSID hidden by creating an HKCU mask key under the scope's shellex\ContextMenuHandlers.</summary>
    HkcuMask,
    /// <summary>Modern PackagedCom (and any) shell extension hidden by adding its CLSID as a REG_SZ to the Shell Extensions Blocked list.</summary>
    BlockedShellExt
}
