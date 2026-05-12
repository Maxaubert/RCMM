using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public sealed class ClassicVerbScanner
{
    private readonly IRegistry _reg;
    private readonly IMuiStringResolver _mui;

    public ClassicVerbScanner(IRegistry reg, IMuiStringResolver mui)
    {
        _reg = reg;
        _mui = mui;
    }

    public IEnumerable<ContextMenuEntry> Scan(Scope scope)
    {
        var root = scope.ToRegistryRoot() + @"\shell";
        if (!_reg.KeyExists(RegistryHive.ClassesRoot, root)) yield break;

        foreach (var name in _reg.GetSubKeyNames(RegistryHive.ClassesRoot, root))
        {
            var path = root + "\\" + name;
            var rawDisplay = _reg.GetValue(RegistryHive.ClassesRoot, path, "") as string;
            var muiVerb = _reg.GetValue(RegistryHive.ClassesRoot, path, "MUIVerb") as string;

            var resolved = _mui.Resolve(muiVerb) ?? _mui.Resolve(rawDisplay);
            if (string.IsNullOrWhiteSpace(resolved))
                continue;  // no friendly display; drop

            var display = StripAccelerator(resolved);
            var hidden = _reg.GetValue(RegistryHive.ClassesRoot, path, "LegacyDisable") != null;
            var commandLine = _reg.GetValue(RegistryHive.ClassesRoot, path + @"\command", "") as string;
            var icon = _reg.GetValue(RegistryHive.ClassesRoot, path, "Icon") as string;

            yield return new ContextMenuEntry
            {
                Id = $"{scope}/shell/{name}",
                DisplayName = display,
                Source = "Unknown",
                Scope = scope,
                Kind = EntryKind.ShellVerb,
                RegistryPath = path,
                OriginalKeyName = name,
                IsBuiltIn = false,
                IsHidden = hidden,
                CommandLine = commandLine,
                IconPath = icon
            };
        }
    }

    private static string StripAccelerator(string s)
        => s.Replace("&&", "￾").Replace("&", "").Replace("￾", "&");
}
