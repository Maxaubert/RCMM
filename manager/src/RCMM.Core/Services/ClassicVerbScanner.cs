using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public sealed class ClassicVerbScanner
{
    private readonly IRegistry _reg;

    public ClassicVerbScanner(IRegistry reg) { _reg = reg; }

    public IEnumerable<ContextMenuEntry> Scan(Scope scope)
    {
        var root = scope.ToRegistryRoot() + @"\shell";
        if (!_reg.KeyExists(RegistryHive.ClassesRoot, root)) yield break;

        foreach (var name in _reg.GetSubKeyNames(RegistryHive.ClassesRoot, root))
        {
            var path = root + "\\" + name;
            var display = _reg.GetValue(RegistryHive.ClassesRoot, path, "") as string;
            var hidden = _reg.GetValue(RegistryHive.ClassesRoot, path, "LegacyDisable") != null;
            var commandLine = _reg.GetValue(RegistryHive.ClassesRoot, path + @"\command", "") as string;

            yield return new ContextMenuEntry
            {
                Id = $"{scope}/shell/{name}",
                DisplayName = string.IsNullOrEmpty(display) ? name : display!,
                Source = "Unknown",
                Scope = scope,
                Kind = EntryKind.ShellVerb,
                RegistryPath = path,
                OriginalKeyName = name,
                IsBuiltIn = false,
                IsHidden = hidden,
                CommandLine = commandLine
            };
        }
    }
}
