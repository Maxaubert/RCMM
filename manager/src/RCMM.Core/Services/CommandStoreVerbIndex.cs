using System;
using System.Collections.Generic;
using RCMM.Core.Diagnostics;

namespace RCMM.Core.Services;

/// <summary>
/// Indexes Windows' built-in modern verbs under
/// HKLM\Software\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell\Windows.* .
/// Each entry's ExplorerCommandHandler is the CLSID that emits the verb at runtime —
/// adding that CLSID to the Shell Extensions\Blocked list reliably suppresses the
/// verb without admin (verified empirically with Windows.share / "Share").
///
/// We expose a lookup by normalised verb name so a live captured verb like
/// "Windows.ModernShare" or "copyaspath" can be matched to its CommandStore entry
/// and hidden via the Blocked-list path.
/// </summary>
public sealed class CommandStoreVerbIndex
{
    private const string CommandStoreRoot =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell";
    private const string Cat = "cmdstore";

    // CommandStore entries can route the verb through any of these CLSID-shaped fields.
    // Different verbs use different ones (Windows.share → ExplorerCommandHandler;
    // Windows.copyaspath → VerbHandler), so we index whichever are present and
    // include every one of them when the caller looks up hide targets — blocking the
    // wrong CLSID is harmless, missing the right one is the bug we hit on copyaspath.
    private static readonly string[] HandlerValueNames =
    {
        "ExplorerCommandHandler",
        "VerbHandler",
        "CommandStateHandler",
        "CanonicalName"
    };

    private readonly IRegistry _reg;
    private Dictionary<string, HashSet<string>>? _byNormalisedName;
    private Dictionary<string, string>? _iconByNormalisedName;
    private Dictionary<string, HashSet<string>>? _verbsByClsid;

    public CommandStoreVerbIndex(IRegistry reg) { _reg = reg; }

    public IReadOnlyDictionary<string, HashSet<string>> Build()
    {
        if (_byNormalisedName != null) return _byNormalisedName;

        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var iconMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byClsid = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (!_reg.KeyExists(RegistryHive.LocalMachine, CommandStoreRoot))
        {
            Log.Warn(Cat, "CommandStore not present");
            _byNormalisedName = map;
            _iconByNormalisedName = iconMap;
            _verbsByClsid = byClsid;
            return map;
        }

        foreach (var name in _reg.GetSubKeyNames(RegistryHive.LocalMachine, CommandStoreRoot))
        {
            var path = CommandStoreRoot + "\\" + name;
            var clsids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var valueName in HandlerValueNames)
            {
                if (_reg.GetValue(RegistryHive.LocalMachine, path, valueName) is string raw
                    && LooksLikeClsid(raw))
                {
                    var clsid = raw.Trim().ToUpperInvariant();
                    clsids.Add(clsid);
                    if (!byClsid.TryGetValue(clsid, out var verbs))
                        byClsid[clsid] = verbs = new(StringComparer.OrdinalIgnoreCase);
                    verbs.Add(name);
                }
            }
            if (clsids.Count == 0) continue;

            var iconValue = _reg.GetValue(RegistryHive.LocalMachine, path, "Icon") as string;

            // Index by every label a caller might present:
            //   - raw key name                         (Windows.share)
            //   - key name with "Windows." stripped    (share)
            //   - last dot-separated segment           (extract from Windows.CompressedFile.extract)
            //   - the VerbName value if present        (opencontaining, format, ...)
            Index(map, iconMap, name, clsids, iconValue);
            if (name.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase))
                Index(map, iconMap, name.Substring("Windows.".Length), clsids, iconValue);
            var lastDot = name.LastIndexOf('.');
            if (lastDot >= 0 && lastDot < name.Length - 1)
                Index(map, iconMap, name.Substring(lastDot + 1), clsids, iconValue);
            if (_reg.GetValue(RegistryHive.LocalMachine, path, "VerbName") is string verbName
                && !string.IsNullOrWhiteSpace(verbName))
                Index(map, iconMap, verbName, clsids, iconValue);
        }

        Log.Info(Cat, $"CommandStoreVerbIndex entries={map.Count} icons={iconMap.Count} clsids={byClsid.Count}");
        _byNormalisedName = map;
        _iconByNormalisedName = iconMap;
        _verbsByClsid = byClsid;
        return map;
    }

    /// <summary>
    /// Returns every CommandStore handler CLSID we've indexed. Used by the
    /// rescan pipeline to register them all with ShellexInvoker in one go,
    /// so a single IExplorerCommand probe covers every modern verb — without
    /// this the invoker's once-per-process cache would skip CLSIDs (Modern
    /// Share / SendToMyPhone / CompressTo) that aren't attached to a live
    /// captured menu item.
    /// </summary>
    public IEnumerable<string> AllClsids()
    {
        Build();
        return _verbsByClsid!.Keys;
    }

    /// <summary>
    /// True when the CLSID is the ExplorerCommandHandler (or sibling field)
    /// of any CommandStore verb — i.e., it's a Windows-OS-known modern menu
    /// handler. The view-model uses this to exempt such rows from the
    /// "unobserved CLSID" filter; they're not attached to a live capture on
    /// Windows 11 because Explorer renders them via the new menu code path,
    /// but they're still real menu items the user can hide.
    /// </summary>
    public bool IsKnownClsid(string? clsid)
    {
        if (string.IsNullOrWhiteSpace(clsid)) return false;
        Build();
        return _verbsByClsid!.ContainsKey(clsid!.Trim().ToUpperInvariant());
    }

    /// <summary>
    /// Derives a friendly menu title for a handler CLSID from the CommandStore
    /// verb key names that reference it. Strips "Windows." plus common modifier
    /// prefixes ("Modern", "Ribbon", "Multi", "MultiVerb.") and splits CamelCase
    /// so {e2bf...} (Windows.ModernShare's handler) renders as "Share" rather
    /// than the registry's "Ribbon Modern Share Verb" COM ProgID or the DLL's
    /// "Shell extensions for sharing" FileDescription. Returns null when the
    /// CLSID isn't in any CommandStore verb.
    /// </summary>
    public string? FriendlyTitleForClsid(string? clsid)
    {
        if (string.IsNullOrWhiteSpace(clsid)) return null;
        Build();
        if (!_verbsByClsid!.TryGetValue(clsid!.Trim().ToUpperInvariant(), out var verbs)) return null;
        // A CLSID may back multiple verbs (Windows.share, Windows.SharePrivate,
        // ...). Pick the title with the shortest derived form — typically the
        // canonical short name ("Share") wins over qualified variants
        // ("Share specific users"). On ties, alphabetical for determinism.
        string? best = null;
        foreach (var v in verbs)
        {
            var t = DeriveTitleFromVerb(v);
            if (string.IsNullOrEmpty(t)) continue;
            if (best == null
                || t!.Length < best.Length
                || (t.Length == best.Length && string.CompareOrdinal(t, best) < 0))
                best = t;
        }
        return best;
    }

    private static readonly string[] _modifierPrefixes =
        { "MultiVerb.", "Multi.", "Modern", "Ribbon" };

    private static string? DeriveTitleFromVerb(string verb)
    {
        var s = verb;
        if (s.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase))
            s = s.Substring("Windows.".Length);
        foreach (var prefix in _modifierPrefixes)
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(prefix.Length);
                break;
            }
        }
        s = s.Replace('.', ' ');
        if (string.IsNullOrWhiteSpace(s)) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var token in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(SplitCamelCase(token));
        }
        var split = sb.ToString();
        if (split.Length == 0) return null;
        var lower = split.ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower.Substring(1);
    }

    private static string SplitCamelCase(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i > 0 && char.IsUpper(c) && char.IsLower(s[i - 1]))
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Returns every CommandStore handler CLSID for the verb (empty if none).</summary>
    public IEnumerable<string> LookupClsids(string verb)
    {
        var key = Normalise(verb);
        if (key == null) return Array.Empty<string>();
        return Build().TryGetValue(key, out var clsids) ? clsids : Array.Empty<string>();
    }

    /// <summary>
    /// Returns the Icon hint registered against the CommandStore entry for the verb
    /// (e.g. "@%SystemRoot%\\System32\\imageres.dll,-5302"). Null when the verb has
    /// no CommandStore entry or no Icon value.
    /// </summary>
    public string? LookupIcon(string verb)
    {
        Build();
        var key = Normalise(verb);
        if (key == null) return null;
        return _iconByNormalisedName!.TryGetValue(key, out var icon) ? icon : null;
    }

    private static void Index(Dictionary<string, HashSet<string>> map,
                              Dictionary<string, string> iconMap,
                              string keyName, HashSet<string> clsids, string? icon)
    {
        var norm = Normalise(keyName);
        if (norm == null) return;
        if (!map.TryGetValue(norm, out var existing))
            map[norm] = new HashSet<string>(clsids, StringComparer.OrdinalIgnoreCase);
        else
            foreach (var c in clsids) existing.Add(c);
        if (!string.IsNullOrWhiteSpace(icon) && !iconMap.ContainsKey(norm))
            iconMap[norm] = icon!;
    }

    private static string? Normalise(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static bool LooksLikeClsid(string s)
        => s.Length >= 38 && s.StartsWith('{') && s.EndsWith('}');
}
