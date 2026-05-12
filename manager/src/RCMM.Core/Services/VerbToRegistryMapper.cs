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
        foreach (var scope in AllScopes)
        {
            var root = scope.ToRegistryRoot() + @"\shell\" + verb;
            if (_reg.KeyExists(RegistryHive.ClassesRoot, root))
            {
                yield return new HideTarget(
                    HideKind.LegacyDisable,
                    RegistryHive.ClassesRoot,
                    root,
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
