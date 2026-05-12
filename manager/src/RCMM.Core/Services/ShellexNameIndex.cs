using System;
using System.Collections.Generic;
using System.Linq;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

/// <summary>
/// Builds a display-name → CLSID index across all classic shellex handler registrations.
/// Used to attach an OwnerClsid to captured menu items that didn't carry a canonical verb.
/// </summary>
public sealed class ShellexNameIndex
{
    private static readonly Scope[] AllScopes =
    {
        Scope.Files, Scope.Folders, Scope.Drives, Scope.Background,
        Scope.AllObjects, Scope.Folder
    };

    // Words that appear so often in shellex FileDescriptions or captured menu text
    // that letting them count as a "match" produces false positives.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","of","to","in","with","for","on","by","at","as","is","be",
        "shell","extension","extensions","handler","handlers","menu","context",
        "windows","microsoft","corp","corporation","page","property","module",
        "common","dll","ui","app","application"
    };

    private readonly IRegistry _reg;
    private readonly ClsidResolver _clsids;
    private readonly IFileVersionReader _files;

    public ShellexNameIndex(IRegistry reg, ClsidResolver clsids, IFileVersionReader files)
    {
        _reg = reg;
        _clsids = clsids;
        _files = files;
    }

    public IReadOnlyDictionary<string, string> BuildNameToClsidMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (display, clsid) in EnumerateRegistrations())
        {
            if (!map.ContainsKey(display)) map[display] = clsid;
        }
        return map;
    }

    /// <summary>
    /// Returns a list of (CLSID, distinctive-word-set) pairs that callers can use to
    /// fuzzy-match a captured display name against an installed handler — catches
    /// "Restore previous versions" ↔ "Previous Versions Property Page",
    /// "Uninstall with Revo Uninstaller Pro" ↔ "Revo Uninstaller Pro Extension", etc.
    /// </summary>
    public IReadOnlyList<(string Clsid, HashSet<string> Words)> BuildClsidWordIndex()
    {
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (display, clsid) in EnumerateRegistrations())
        {
            var words = DistinctiveWords(display);
            if (words.Count == 0) continue;
            if (!seen.TryGetValue(clsid, out var existing))
                seen[clsid] = words;
            else
                foreach (var w in words) existing.Add(w);
        }
        return seen.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>
    /// Heuristically returns the most likely shellex CLSID whose registered-DLL
    /// description shares distinctive words with the captured display name. Returns
    /// null when no candidate has 2+ shared distinctive words (single-word matches
    /// are too noisy — every Microsoft component has "Shell" in its description).
    /// </summary>
    public string? FuzzyMatch(string capturedName,
                              IReadOnlyList<(string Clsid, HashSet<string> Words)>? index = null)
    {
        index ??= BuildClsidWordIndex();
        var capturedWords = DistinctiveWords(capturedName);
        if (capturedWords.Count == 0) return null;

        string? best = null;
        int bestShared = 0;
        foreach (var (clsid, words) in index)
        {
            int shared = capturedWords.Count(w => words.Contains(w));
            if (shared >= 2 && shared > bestShared)
            {
                bestShared = shared;
                best = clsid;
            }
        }
        return best;
    }

    private IEnumerable<(string Display, string Clsid)> EnumerateRegistrations()
    {
        foreach (var scope in AllScopes)
        {
            var root = scope.ToRegistryRoot() + @"\shellex\ContextMenuHandlers";
            if (!_reg.KeyExists(RegistryHive.ClassesRoot, root)) continue;
            foreach (var name in _reg.GetSubKeyNames(RegistryHive.ClassesRoot, root))
            {
                var defaultVal = _reg.GetValue(RegistryHive.ClassesRoot, root + "\\" + name, "") as string;
                var clsid = LooksLikeClsid(defaultVal) ? defaultVal! :
                            LooksLikeClsid(name) ? name : null;
                if (clsid == null) continue;
                var resolved = _clsids.Resolve(clsid);
                var version = resolved?.DllPath is { } dll ? _files.Read(dll) : new FileVersion(null, null, null);
                var display = !string.IsNullOrWhiteSpace(version.FileDescription) ? version.FileDescription!.Trim()
                            : !string.IsNullOrWhiteSpace(resolved?.DefaultName) ? resolved!.DefaultName!.Trim()
                            : name;
                yield return (display, clsid.ToUpperInvariant());
            }
        }
    }

    private static HashSet<string> DistinctiveWords(string? text)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text)) return result;
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text + " ")
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else
            {
                if (sb.Length >= 3)
                {
                    var word = sb.ToString();
                    if (!StopWords.Contains(word)) result.Add(word);
                }
                sb.Clear();
            }
        }
        return result;
    }

    private static bool LooksLikeClsid(string? s)
        => !string.IsNullOrEmpty(s) && s.StartsWith('{') && s.EndsWith('}');
}
