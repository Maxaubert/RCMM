using System;
using System.Collections.Generic;
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
                if (!map.ContainsKey(display)) map[display] = clsid;
            }
        }
        return map;
    }

    private static bool LooksLikeClsid(string? s)
        => !string.IsNullOrEmpty(s) && s.StartsWith('{') && s.EndsWith('}');
}
