using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public sealed class ClassicShellexScanner
{
    private readonly IRegistry _reg;
    private readonly ClsidResolver _clsids;

    public ClassicShellexScanner(IRegistry reg, ClsidResolver clsids)
    {
        _reg = reg;
        _clsids = clsids;
    }

    public IEnumerable<ContextMenuEntry> Scan(Scope scope)
    {
        var root = scope.ToRegistryRoot() + @"\shellex\ContextMenuHandlers";
        if (!_reg.KeyExists(RegistryHive.ClassesRoot, root)) yield break;

        foreach (var name in _reg.GetSubKeyNames(RegistryHive.ClassesRoot, root))
        {
            var path = root + "\\" + name;
            var defaultVal = _reg.GetValue(RegistryHive.ClassesRoot, path, "") as string;
            var clsid = LooksLikeClsid(defaultVal) ? defaultVal! :
                        LooksLikeClsid(name) ? name : defaultVal ?? name;

            var resolved = _clsids.Resolve(clsid);
            var display = resolved?.DefaultName ?? name;

            var maskPath = @"Software\Classes\" + scope.ToRegistryRoot() + @"\shellex\ContextMenuHandlers\" + name;
            var hidden = _reg.KeyExists(RegistryHive.CurrentUser, maskPath);

            yield return new ContextMenuEntry
            {
                Id = $"{scope}/shellex/{name}",
                DisplayName = display,
                Source = "Unknown",
                Scope = scope,
                Kind = EntryKind.ShellExtension,
                RegistryPath = path,
                OriginalKeyName = name,
                IsBuiltIn = false,
                IsHidden = hidden,
                Clsid = clsid
            };
        }
    }

    private static bool LooksLikeClsid(string? s)
        => !string.IsNullOrEmpty(s) && s.StartsWith('{') && s.EndsWith('}');
}
