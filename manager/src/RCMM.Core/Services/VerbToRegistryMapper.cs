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

    /// <summary>Lazily built verb → association buckets index (see
    /// <see cref="BuildAssociationVerbIndex"/>). Rebuilt on the next MapVerb after
    /// <see cref="InvalidateAssociationCache"/> so a rescan sees newly installed apps.</summary>
    private Dictionary<string, List<string>>? _assocVerbIndex;

    public void InvalidateAssociationCache() => _assocVerbIndex = null;

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

        // File-association progids (VLC.mp4, txtfile, …) register per-file-type verbs
        // at <progid>\shell\<verb> — this is where most media/document app entries
        // actually live, and the association array Explorer builds for a file draws
        // from them. They are NOT under any scope root or SystemFileAssociations, so
        // without this branch RCMM can neither detect nor hide them: the classic
        // failure was VLC's PlayWithVLC, whose only mapped target was the folder-scope
        // Directory\shell key while the visible .mp4 menu item came from VLC.mp4.
        _assocVerbIndex ??= BuildAssociationVerbIndex();
        if (_assocVerbIndex.TryGetValue(verb, out var buckets))
        {
            foreach (var bucket in buckets)
            {
                yield return new HideTarget(
                    HideKind.LegacyDisable,
                    RegistryHive.CurrentUser,
                    @"Software\Classes\" + bucket + @"\shell\" + verb,
                    "LegacyDisable");
            }
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

    /// <summary>
    /// Builds the verb → association-bucket index. A "bucket" is any HKCR key whose
    /// shell\* verbs can surface in a file's context menu via the association array:
    /// the extension key itself (.mp4) plus every progid an extension references —
    /// its default value, its OpenWithProgids value names, and the per-user
    /// Explorer FileExts\*\UserChoice ProgId (which overrides the default and is
    /// exactly how "VLC is my .mp4 player" gets wired up). Buckets referenced by any
    /// extension are included even when not currently the default: hiding a verb
    /// should survive the user switching default apps between that vendor's progids.
    /// </summary>
    private Dictionary<string, List<string>> BuildAssociationVerbIndex()
    {
        var buckets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _reg.GetSubKeyNames(RegistryHive.ClassesRoot, ""))
        {
            if (name.Length < 2 || name[0] != '.') continue;
            if (_reg.KeyExists(RegistryHive.ClassesRoot, name + @"\shell"))
                buckets.Add(name);
            if (_reg.GetValue(RegistryHive.ClassesRoot, name, "") is string progid
                && progid.Length > 0 && !progid.Contains('\\'))
                buckets.Add(progid);
            foreach (var p in _reg.GetValueNames(RegistryHive.ClassesRoot, name + @"\OpenWithProgids"))
                if (p.Length > 0 && !p.Contains('\\'))
                    buckets.Add(p);
        }

        const string fileExts = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";
        foreach (var ext in _reg.GetSubKeyNames(RegistryHive.CurrentUser, fileExts))
            if (_reg.GetValue(RegistryHive.CurrentUser, fileExts + "\\" + ext + @"\UserChoice", "ProgId") is string chosen
                && chosen.Length > 0 && !chosen.Contains('\\'))
                buckets.Add(chosen);

        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var bucket in buckets)
        {
            foreach (var verb in _reg.GetSubKeyNames(RegistryHive.ClassesRoot, bucket + @"\shell"))
            {
                if (!index.TryGetValue(verb, out var list))
                    index[verb] = list = new List<string>();
                list.Add(bucket);
            }
        }
        return index;
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
