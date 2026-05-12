using System;
using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public sealed class ClassicVerbScanner
{
    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();

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
            var muiVerbHint = _reg.GetValue(RegistryHive.ClassesRoot, path, "MUIVerb") as string;

            var isBuiltIn = LooksWindowsPath(commandLine)
                            || LooksWindowsPath(icon)
                            || LooksWindowsPath(muiVerbHint)
                            || LooksBareSystemMuiReference(muiVerbHint)
                            || LooksBareSystemMuiReference(rawDisplay);

            yield return new ContextMenuEntry
            {
                Id = $"{scope}/shell/{name}",
                DisplayName = display,
                Source = isBuiltIn ? "Windows" : "Unknown",
                Scope = scope,
                Kind = EntryKind.ShellVerb,
                RegistryPath = path,
                OriginalKeyName = name,
                IsBuiltIn = isBuiltIn,
                IsHidden = hidden,
                CommandLine = commandLine,
                IconPath = icon
            };
        }
    }

    private static string StripAccelerator(string s)
        => s.Replace("&&", "￾").Replace("&", "").Replace("￾", "&");

    /// <summary>
    /// True when the value is an MUI indirect-string reference whose DLL is a bare
    /// filename (no path). Bare DLL names only resolve via Windows' system search
    /// path (System32/SysWOW64/PATH), so a successful registration here almost
    /// always points at a Windows system component (e.g. "@efscore.dll,-101").
    /// </summary>
    private static bool LooksBareSystemMuiReference(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw[0] != '@') return false;
        var s = raw[1..].Trim();
        var comma = s.LastIndexOf(',');
        if (comma > 0 && comma > s.LastIndexOf('\\')) s = s[..comma];
        if (s.Contains('\\') || s.Contains('/')) return false;
        if (s.IndexOf('%') >= 0) return false;
        return s.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || s.EndsWith(".mui", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksWindowsPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try
        {
            var s = Environment.ExpandEnvironmentVariables(raw).ToLowerInvariant();
            if (s.Length > 0 && s[0] == '@') s = s[1..];
            if (s.StartsWith('"'))
            {
                var end = s.IndexOf('"', 1);
                if (end > 1) s = s[1..end];
            }
            else
            {
                var space = s.IndexOf(' ');
                if (space > 0) s = s[..space];
            }
            var comma = s.LastIndexOf(',');
            if (comma > 0 && comma > s.LastIndexOf('\\')) s = s[..comma];

            if (WindowsDir.Length > 0 && s.StartsWith(WindowsDir + "\\")) return true;
            return s.Contains(@"\windows\system32\") || s.Contains(@"\windows\syswow64\");
        }
        catch
        {
            return false;
        }
    }
}
