using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

/// <summary>
/// The single walker over <c>&lt;root&gt;\shellex\ContextMenuHandlers</c>
/// registrations. Five services used to re-implement this walk privately, and
/// only two of them carried the HKCU-mask recovery — so mask-related fixes had
/// to be re-discovered per walker. Every call site now states its
/// <c>recoverMaskedDefault</c> choice explicitly.
/// </summary>
internal static class ShellexHandlerWalk
{
    /// <summary>The six standard context-menu scope roots every scanner and
    /// mapper walks. Previously copied into five classes.</summary>
    internal static readonly Scope[] StandardScopes =
    {
        Scope.Files, Scope.Folders, Scope.Drives, Scope.Background,
        Scope.AllObjects, Scope.Folder
    };

    /// <summary>One handler registration key: its name, its (possibly
    /// mask-recovered) default value, and the CLSID when either the default or
    /// the key name is CLSID-shaped.</summary>
    internal readonly record struct Registration(string KeyName, string? RawDefault, string? Clsid);

    /// <summary>
    /// Enumerates the handler registrations under one ContextMenuHandlers root.
    ///
    /// <paramref name="recoverMaskedDefault"/>: RCMM's HkcuMask hide gives the
    /// HKCU\Software\Classes shadow key an empty default value, which wins in
    /// HKCR's merged view — so a hidden handler's CLSID reads back empty here
    /// even though the real registration still exists. When true, the walk
    /// recovers it: first from the RCMM.SavedDefault stash Hide wrote into the
    /// masked key (the ONLY copy for a handler whose real registration was
    /// per-user, with no HKLM twin), then from HKLM for masked machine-wide
    /// registrations. Callers that only care about handlers currently visible
    /// to the shell pass false — deliberately, not by omission.
    /// </summary>
    internal static IEnumerable<Registration> EnumerateHandlers(
        IRegistry reg, string handlersRoot, bool recoverMaskedDefault)
    {
        if (!reg.KeyExists(RegistryHive.ClassesRoot, handlersRoot)) yield break;
        foreach (var name in reg.GetSubKeyNames(RegistryHive.ClassesRoot, handlersRoot))
        {
            var path = handlersRoot + "\\" + name;
            var defaultVal = reg.GetValue(RegistryHive.ClassesRoot, path, "") as string;
            if (recoverMaskedDefault)
            {
                if (string.IsNullOrEmpty(defaultVal))
                    defaultVal = reg.GetValue(RegistryHive.CurrentUser,
                        "Software\\Classes\\" + path, HideService.SavedDefaultValueName) as string;
                if (string.IsNullOrEmpty(defaultVal))
                    defaultVal = reg.GetValue(RegistryHive.LocalMachine,
                        "Software\\Classes\\" + path, "") as string;
            }
            var clsid = RegistryHeuristics.LooksLikeClsid(defaultVal) ? defaultVal :
                        RegistryHeuristics.LooksLikeClsid(name) ? name : null;
            yield return new Registration(name, defaultVal, clsid);
        }
    }
}
