using System;
using System.Collections.Generic;
using RCMM.Core.Diagnostics;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

/// <summary>
/// Maps the normalised shellex key name to every (scope, raw key name) the
/// handler is registered under. Used to attach hide targets to live-captured
/// menu items that the shell emits with no canonical verb — typically
/// "Send to" → SendTo, "Open with" → Open With, "Sharing" → ModernSharing, etc.
/// </summary>
public sealed class ShellexKeyNameIndex
{
    private readonly IRegistry _reg;
    private Dictionary<string, List<(Scope scope, string keyName)>>? _byNormalisedName;

    public ShellexKeyNameIndex(IRegistry reg) { _reg = reg; }

    public IReadOnlyDictionary<string, List<(Scope scope, string keyName)>> Build()
    {
        if (_byNormalisedName != null) return _byNormalisedName;

        var map = new Dictionary<string, List<(Scope, string)>>(StringComparer.OrdinalIgnoreCase);
        int entryCount = 0;
        foreach (var scope in ShellexHandlerWalk.StandardScopes)
        {
            var handlersRoot = scope.ToRegistryRoot() + @"\shellex\ContextMenuHandlers";
            // recoverMaskedDefault FALSE (and the registration's CLSID unused):
            // this index maps key NAMES to hide targets, so the default value
            // never matters — a masked key still has its name.
            foreach (var r in ShellexHandlerWalk.EnumerateHandlers(_reg, handlersRoot, recoverMaskedDefault: false))
            {
                var name = r.KeyName;
                var norm = Normalise(name);
                if (norm == null) continue;
                if (!map.TryGetValue(norm, out var list)) map[norm] = list = new();
                list.Add((scope, name));
                entryCount++;
            }
        }

        Log.Info("shellexkey", $"ShellexKeyNameIndex normalisedKeys={map.Count} totalRegs={entryCount}");
        _byNormalisedName = map;
        return map;
    }

    /// <summary>
    /// Yields HKCU mask HideTargets for any shellex registration whose key name
    /// normalises to the same form as the captured display name.
    /// </summary>
    public IEnumerable<HideTarget> MapDisplayName(string displayName)
    {
        var norm = Normalise(displayName);
        if (norm == null) yield break;
        if (!Build().TryGetValue(norm, out var matches)) yield break;

        foreach (var (scope, keyName) in matches)
        {
            yield return new HideTarget(
                HideKind.HkcuMask,
                RegistryHive.CurrentUser,
                @"Software\Classes\" + scope.ToRegistryRoot() + @"\shellex\ContextMenuHandlers\" + keyName,
                null);
        }
    }

    private static string? Normalise(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        // Skip CLSID-style key names — those aren't friendly-text matches.
        if (RegistryHeuristics.LooksLikeClsid(s)) return null;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.Length == 0 ? null : sb.ToString();
    }
}
