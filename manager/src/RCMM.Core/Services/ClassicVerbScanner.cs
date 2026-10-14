using System;
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
            {
                // A verb with neither a default value nor MUIVerb still renders
                // in Explorer — as the key name itself (a common hand-written
                // registry-tweak pattern). Dropping it here made a verb that was
                // pre-hidden by another tool (LegacyDisable written before RCMM
                // ever scanned) unrecoverable from the UI: the live capture
                // can't see a disabled verb, and this scan was its only other
                // discovery path. Fall back to the key name; the visible-list
                // filter still applies its normal rules to the result. A verb
                // whose display value merely failed MUI resolution stays
                // dropped — the author intended a localized label we can't
                // reproduce, and guessing the key name would mislabel it.
                if (!string.IsNullOrWhiteSpace(muiVerb) || !string.IsNullOrWhiteSpace(rawDisplay))
                    continue;
                resolved = name;
            }

            var display = StripAccelerator(resolved);
            var commandLine = _reg.GetValue(RegistryHive.ClassesRoot, path + @"\command", "") as string;
            var icon = _reg.GetValue(RegistryHive.ClassesRoot, path, "Icon") as string;

            yield return new ContextMenuEntry
            {
                Id = $"{scope}/shell/{name}",
                DisplayName = display,
                Scope = scope,
                Kind = EntryKind.ShellVerb,
                RegistryPath = path,
                OriginalKeyName = name,
                CommandLine = commandLine,
                IconPath = icon
            };
        }
    }

    private static string StripAccelerator(string s)
        => s.Replace("&&", "￾").Replace("&", "").Replace("￾", "&");
}
