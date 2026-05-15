using System;
using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public sealed class VerbToRegistryMapper
{
    private static readonly Scope[] AllScopes =
    {
        Scope.Files, Scope.Folders, Scope.Drives, Scope.Background,
        Scope.AllObjects, Scope.Folder
    };

    private readonly IRegistry _reg;

    public VerbToRegistryMapper(IRegistry reg) { _reg = reg; }

    public IEnumerable<HideTarget> MapVerb(string verb)
    {
        // We READ from HKCR (the merged view) to find verb registrations that exist
        // anywhere, but we WRITE into HKCU\Software\Classes — the per-user override
        // of HKCR. The merged HKCR view picks up LegacyDisable from there, so the
        // verb is suppressed for this user without needing admin to write HKLM.
        foreach (var scope in AllScopes)
        {
            var hkcrPath = scope.ToRegistryRoot() + @"\shell\" + verb;
            if (!_reg.KeyExists(RegistryHive.ClassesRoot, hkcrPath)) continue;
            yield return new HideTarget(
                HideKind.LegacyDisable,
                RegistryHive.CurrentUser,
                @"Software\Classes\" + hkcrPath,
                "LegacyDisable");
        }

        // SystemFileAssociations\<ext-or-type>\shell\<verb> registers verbs that only
        // apply to files of a given extension or perceived type (image, audio, …).
        // This is where things like ShareXImageEditor live. We enumerate all subkeys
        // once and check each for the verb; for verbs not registered under any SFA
        // bucket it's a cheap KeyExists per bucket.
        const string sfaRoot = "SystemFileAssociations";
        if (_reg.KeyExists(RegistryHive.ClassesRoot, sfaRoot))
        {
            foreach (var sfa in _reg.GetSubKeyNames(RegistryHive.ClassesRoot, sfaRoot))
            {
                var hkcrPath = sfaRoot + "\\" + sfa + @"\shell\" + verb;
                if (!_reg.KeyExists(RegistryHive.ClassesRoot, hkcrPath)) continue;
                yield return new HideTarget(
                    HideKind.LegacyDisable,
                    RegistryHive.CurrentUser,
                    @"Software\Classes\" + hkcrPath,
                    "LegacyDisable");
            }
        }
    }

    public IEnumerable<HideTarget> MapClsid(string clsid)
    {
        foreach (var scope in AllScopes)
        {
            var handlersRoot = scope.ToRegistryRoot() + @"\shellex\ContextMenuHandlers";
            if (!_reg.KeyExists(RegistryHive.ClassesRoot, handlersRoot)) continue;
            foreach (var name in _reg.GetSubKeyNames(RegistryHive.ClassesRoot, handlersRoot))
            {
                var defaultVal = _reg.GetValue(RegistryHive.ClassesRoot, handlersRoot + "\\" + name, "") as string;
                // HKCU mask path: once the user has hidden a shellex, the HKCU
                // shadow key we wrote has an empty default value, which wins
                // in HKCR's merged view. The classic CLSID lookup then fails
                // and ResolveHideTargets falls back to BlockedShellExt — but
                // the *original* hide was HkcuMask, so on un-hide we'd leave
                // the HkcuMask keys orphaned. Fall back to HKLM to recover
                // the original CLSID through the user's own mask.
                if (string.IsNullOrEmpty(defaultVal))
                    defaultVal = _reg.GetValue(RegistryHive.LocalMachine,
                        "Software\\Classes\\" + handlersRoot + "\\" + name, "") as string;
                var match = (defaultVal != null && string.Equals(defaultVal, clsid, StringComparison.OrdinalIgnoreCase))
                            || string.Equals(name, clsid, StringComparison.OrdinalIgnoreCase);
                if (match)
                {
                    yield return new HideTarget(
                        HideKind.HkcuMask,
                        RegistryHive.CurrentUser,
                        @"Software\Classes\" + scope.ToRegistryRoot() + @"\shellex\ContextMenuHandlers\" + name,
                        null);
                }
            }
        }
    }
}
