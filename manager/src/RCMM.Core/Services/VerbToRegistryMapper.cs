using System;
using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public sealed class VerbToRegistryMapper
{
    private static readonly Scope[] AllScopes = ShellexHandlerWalk.StandardScopes;

    private readonly IRegistry _reg;

    public VerbToRegistryMapper(IRegistry reg) { _reg = reg; }

    /// <summary>Per-extension association-bucket cache (see
    /// <see cref="GetAssociationBuckets"/>). Rebuilt lazily after
    /// <see cref="InvalidateAssociationCache"/> so a rescan sees newly installed apps.</summary>
    private Dictionary<string, IReadOnlyList<string>>? _bucketsByExtension;

    public void InvalidateAssociationCache() => _bucketsByExtension = null;

    /// <summary>Maps a verb with no known source registrations: only the six scope
    /// roots are checked. File-association and SystemFileAssociations targets need
    /// <paramref name="sourceExtensions"/> — mapping them without knowing where the
    /// verb was actually observed is exactly the cross-progid fan-out this class
    /// used to have (see the overload's comment).</summary>
    public IEnumerable<HideTarget> MapVerb(string verb) => MapVerb(verb, null);

    public IEnumerable<HideTarget> MapVerb(string verb, IReadOnlyCollection<string>? sourceExtensions)
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
        // from them. They are NOT under any scope root, so without this branch RCMM
        // can neither detect nor hide them: the classic failure was VLC's
        // PlayWithVLC, whose only mapped target was the folder-scope Directory\shell
        // key while the visible .mp4 menu item came from VLC.mp4.
        //
        // Crucially the fan-out is scoped to sourceExtensions — the sample-file
        // extensions the verb was actually captured on. An earlier build indexed
        // every bucket on the machine by bare verb name, so hiding one "Print" row
        // wrote LegacyDisable into txtfile, batfile, jpegfile and every other progid
        // that happened to carry a shell\print key — silently killing the verb
        // menu-wide and breaking ShellExecute("print") for unrelated apps. Only the
        // registrations that produced the visible row may be touched.
        if (sourceExtensions is { Count: > 0 })
        {
            // One verb can be observed on several extensions whose buckets overlap
            // (a progid referenced by both .mp4's default and .m4v's OpenWithProgids)
            // — dedupe on the final HKCR path.
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ext in sourceExtensions)
            {
                foreach (var bucket in GetAssociationBuckets(ext))
                {
                    var hkcrPath = bucket + @"\shell\" + verb;
                    if (!emitted.Add(hkcrPath)) continue;
                    if (!_reg.KeyExists(RegistryHive.ClassesRoot, hkcrPath)) continue;
                    yield return new HideTarget(
                        HideKind.LegacyDisable,
                        RegistryHive.CurrentUser,
                        @"Software\Classes\" + hkcrPath,
                        "LegacyDisable");
                }

                // SystemFileAssociations\<ext-or-perceived-type>\shell\<verb> registers
                // verbs that only apply to files of a given extension or perceived type
                // (image, audio, …) — this is where things like ShareXImageEditor live.
                // Explorer only consults the SFA buckets for the file's own extension
                // and its PerceivedType, so those are the only two we may map: scanning
                // every SFA subkey for the verb name is the same cross-type fan-out as
                // the association index above.
                foreach (var sfa in GetSfaBuckets(ext))
                {
                    var hkcrPath = @"SystemFileAssociations\" + sfa + @"\shell\" + verb;
                    if (!emitted.Add(hkcrPath)) continue;
                    if (!_reg.KeyExists(RegistryHive.ClassesRoot, hkcrPath)) continue;
                    yield return new HideTarget(
                        HideKind.LegacyDisable,
                        RegistryHive.CurrentUser,
                        @"Software\Classes\" + hkcrPath,
                        "LegacyDisable");
                }
            }
        }
    }

    /// <summary>
    /// The association buckets for ONE extension — the HKCR keys whose shell\*
    /// verbs Explorer's association array actually consults for a file of that
    /// type: the extension key itself (.mp4), the extension's default progid,
    /// its OpenWithProgids value names, and the per-user Explorer
    /// FileExts\&lt;ext&gt;\UserChoice ProgId (which overrides the default and is
    /// exactly how "VLC is my .mp4 player" gets wired up). Non-default progids the
    /// extension references are included: hiding a verb should survive the user
    /// switching default apps among the apps registered for that extension.
    /// </summary>
    private IReadOnlyList<string> GetAssociationBuckets(string ext)
    {
        _bucketsByExtension ??= new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (_bucketsByExtension.TryGetValue(ext, out var cached)) return cached;

        var buckets = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? b)
        {
            if (!string.IsNullOrEmpty(b) && !b.Contains('\\') && seen.Add(b!)) buckets.Add(b!);
        }

        Add(ext);
        Add(_reg.GetValue(RegistryHive.ClassesRoot, ext, "") as string);
        foreach (var p in _reg.GetValueNames(RegistryHive.ClassesRoot, ext + @"\OpenWithProgids"))
            Add(p);
        const string fileExts = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";
        Add(_reg.GetValue(RegistryHive.CurrentUser, fileExts + "\\" + ext + @"\UserChoice", "ProgId") as string);

        _bucketsByExtension[ext] = buckets;
        return buckets;
    }

    /// <summary>The SystemFileAssociations buckets Explorer consults for a file of
    /// the given extension: the extension itself and its PerceivedType (image,
    /// audio, video, text, …).</summary>
    private IEnumerable<string> GetSfaBuckets(string ext)
    {
        yield return ext;
        if (_reg.GetValue(RegistryHive.ClassesRoot, ext, "PerceivedType") is string perceived
            && perceived.Length > 0 && !perceived.Contains('\\'))
            yield return perceived;
    }

    public IEnumerable<HideTarget> MapClsid(string clsid)
    {
        foreach (var scope in AllScopes)
        {
            var handlersRoot = scope.ToRegistryRoot() + @"\shellex\ContextMenuHandlers";
            // recoverMaskedDefault: once the user has hidden a shellex, the HKCU
            // shadow key we wrote has an empty default value, which wins in
            // HKCR's merged view. The classic CLSID lookup then fails and
            // ResolveHideTargets falls back to BlockedShellExt — but the
            // *original* hide was HkcuMask, so on un-hide we'd leave the
            // HkcuMask keys orphaned. The walk recovers the original CLSID
            // through the user's own mask (RCMM.SavedDefault stash first, then
            // HKLM) — without it a hidden per-user handler is unrecoverable
            // after a rescan. See ShellexHandlerWalk.
            foreach (var r in ShellexHandlerWalk.EnumerateHandlers(_reg, handlersRoot, recoverMaskedDefault: true))
            {
                var match = (r.RawDefault != null && string.Equals(r.RawDefault, clsid, StringComparison.OrdinalIgnoreCase))
                            || string.Equals(r.KeyName, clsid, StringComparison.OrdinalIgnoreCase);
                if (match)
                {
                    yield return new HideTarget(
                        HideKind.HkcuMask,
                        RegistryHive.CurrentUser,
                        @"Software\Classes\" + scope.ToRegistryRoot() + @"\shellex\ContextMenuHandlers\" + r.KeyName,
                        null);
                }
            }
        }
    }
}
