using System;
using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public sealed class ClassicShellexScanner
{
    private readonly IRegistry _reg;
    private readonly ClsidResolver _clsids;
    private readonly IFileVersionReader _files;

    public ClassicShellexScanner(IRegistry reg, ClsidResolver clsids, IFileVersionReader files)
    {
        _reg = reg;
        _clsids = clsids;
        _files = files;
    }

    public IEnumerable<ContextMenuEntry> Scan(Scope scope)
    {
        var root = scope.ToRegistryRoot() + @"\shellex\ContextMenuHandlers";
        // recoverMaskedDefault: a hidden handler's merged-HKCR default reads
        // back empty (the HKCU mask wins); recovering the CLSID keeps the row
        // in RCMM's list as a hidden entry the user can toggle back instead of
        // vanishing entirely. See ShellexHandlerWalk.
        foreach (var r in ShellexHandlerWalk.EnumerateHandlers(_reg, root, recoverMaskedDefault: true))
        {
            var name = r.KeyName;
            var path = root + "\\" + name;
            var clsid = r.Clsid ?? r.RawDefault ?? name;

            var resolved = _clsids.Resolve(clsid);
            var version = resolved?.DllPath is { } dll ? _files.Read(dll) : new FileVersion(null, null, null);

            var display = PickDisplay(version.FileDescription, resolved?.DefaultName, name);
            if (display is null) continue;   // no real display name; drop

            yield return new ContextMenuEntry
            {
                Id = $"{scope}/shellex/{name}",
                DisplayName = display,
                Scope = scope,
                Kind = EntryKind.ShellExtension,
                RegistryPath = path,
                OriginalKeyName = name,
                Clsid = clsid,
                IconPath = resolved?.DllPath
            };
        }
    }

    private static string? PickDisplay(string? fileDescription, string? defaultName, string keyName)
    {
        if (!string.IsNullOrWhiteSpace(fileDescription)) return fileDescription!.Trim();
        if (!string.IsNullOrWhiteSpace(defaultName) &&
            !string.Equals(defaultName, keyName, StringComparison.OrdinalIgnoreCase))
            return defaultName!.Trim();
        return null;
    }
}
