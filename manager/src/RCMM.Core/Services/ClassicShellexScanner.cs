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
        if (!_reg.KeyExists(RegistryHive.ClassesRoot, root)) yield break;

        foreach (var name in _reg.GetSubKeyNames(RegistryHive.ClassesRoot, root))
        {
            var path = root + "\\" + name;
            var defaultVal = _reg.GetValue(RegistryHive.ClassesRoot, path, "") as string;
            var clsid = LooksLikeClsid(defaultVal) ? defaultVal! :
                        LooksLikeClsid(name) ? name : defaultVal ?? name;

            var resolved = _clsids.Resolve(clsid);
            var version = resolved?.DllPath is { } dll ? _files.Read(dll) : new FileVersion(null, null, null);

            var display = PickDisplay(version.FileDescription, resolved?.DefaultName, name);
            if (display is null) continue;   // no real display name; drop

            var source = version.CompanyName ?? "Unknown";
            var isBuiltIn = LooksMicrosoft(version.CompanyName);

            var maskPath = @"Software\Classes\" + scope.ToRegistryRoot() + @"\shellex\ContextMenuHandlers\" + name;
            var hidden = _reg.KeyExists(RegistryHive.CurrentUser, maskPath);

            yield return new ContextMenuEntry
            {
                Id = $"{scope}/shellex/{name}",
                DisplayName = display,
                Source = source,
                Scope = scope,
                Kind = EntryKind.ShellExtension,
                RegistryPath = path,
                OriginalKeyName = name,
                IsBuiltIn = isBuiltIn,
                IsHidden = hidden,
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

    private static bool LooksMicrosoft(string? company)
        => !string.IsNullOrEmpty(company) &&
           company.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool LooksLikeClsid(string? s)
        => !string.IsNullOrEmpty(s) && s.StartsWith('{') && s.EndsWith('}');
}
