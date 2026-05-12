using System;
using System.Collections.Generic;
using RCMM.Core.Diagnostics;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

/// <summary>
/// Enumerates packaged COM context menu candidates from
/// HKLM\SOFTWARE\Classes\PackagedCom\Package\&lt;pkg&gt;.
///
/// Heuristic: a Server\&lt;idx&gt; entry that has a SurrogateAppId and no Executable
/// is a COM surrogate activation — the path Explorer uses for modern context
/// menus. Entries with Executable (toast activators, launch handlers) are skipped.
/// </summary>
public sealed class PackagedShellExtScanner
{
    private const string PackagesRoot = @"SOFTWARE\Classes\PackagedCom\Package";
    private const string AppxStoreRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications";
    private const string Cat = "packaged";

    private readonly IRegistry _reg;
    private readonly IMuiStringResolver? _mui;

    public PackagedShellExtScanner(IRegistry reg, IMuiStringResolver? mui = null)
    {
        _reg = reg;
        _mui = mui;
    }

    public IEnumerable<PackagedShellExt> Scan()
    {
        if (!_reg.KeyExists(RegistryHive.LocalMachine, PackagesRoot))
        {
            Log.Warn(Cat, "PackagedCom\\Package root not present — skipping packaged scan");
            yield break;
        }

        int packages = 0;
        int yielded = 0;
        foreach (var pkg in _reg.GetSubKeyNames(RegistryHive.LocalMachine, PackagesRoot))
        {
            packages++;
            var serverRoot = PackagesRoot + "\\" + pkg + "\\Server";
            if (!_reg.KeyExists(RegistryHive.LocalMachine, serverRoot)) continue;

            string? installFolder = ResolvePackageInstallFolder(pkg);

            foreach (var idx in _reg.GetSubKeyNames(RegistryHive.LocalMachine, serverRoot))
            {
                var serverPath = serverRoot + "\\" + idx;
                var executable = _reg.GetValue(RegistryHive.LocalMachine, serverPath, "Executable") as string;
                if (!string.IsNullOrEmpty(executable)) continue;

                var clsid = _reg.GetValue(RegistryHive.LocalMachine, serverPath, "SurrogateAppId") as string;
                if (string.IsNullOrWhiteSpace(clsid) || !LooksLikeClsid(clsid)) continue;

                var displayName = ResolveString((_reg.GetValue(RegistryHive.LocalMachine, serverPath, "DisplayName") as string)?.Trim());
                var appDisplayName = ResolveString((_reg.GetValue(RegistryHive.LocalMachine, serverPath, "ApplicationDisplayName") as string)?.Trim());

                var chosenDisplay = !string.IsNullOrWhiteSpace(displayName) ? displayName
                                  : !string.IsNullOrWhiteSpace(appDisplayName) ? appDisplayName
                                  : pkg;
                var publisher = !string.IsNullOrWhiteSpace(appDisplayName) ? appDisplayName : pkg;

                // The Class\<CLSID>\DllPath is relative to the package install folder.
                // Joining the two gives ExtractIconEx an absolute path it can probe.
                var classPath = PackagesRoot + "\\" + pkg + "\\Class\\" + clsid!.Trim();
                var relDll = _reg.GetValue(RegistryHive.LocalMachine, classPath, "DllPath") as string;
                string? absDll = null;
                if (!string.IsNullOrWhiteSpace(relDll) && installFolder != null)
                    absDll = System.IO.Path.Combine(installFolder, relDll!);

                yielded++;
                yield return new PackagedShellExt
                {
                    Clsid = clsid.Trim().ToUpperInvariant(),
                    PackageFullName = pkg,
                    DisplayName = chosenDisplay!,
                    PublisherDisplayName = publisher!,
                    DllPath = absDll
                };
            }
        }

        Log.Info(Cat, $"PackagedShellExtScanner packages={packages} candidates={yielded}");
    }

    private string? ResolvePackageInstallFolder(string packageFullName)
    {
        // Primary: AppxAllUserStore lists a manifest path for some (but not all)
        // packages. AMD lives here; Clipchamp / Terminal / Notepad++ do not.
        var path = _reg.GetValue(RegistryHive.LocalMachine,
            AppxStoreRoot + "\\" + packageFullName, "Path") as string;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try { var dir = System.IO.Path.GetDirectoryName(path); if (dir != null) return dir; }
            catch { }
        }

        // Fallback: the standard system-wide install location. Even when the WindowsApps
        // folder is unenumerable for non-admin users, ExtractIconEx can still read a DLL
        // by absolute path inside it.
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            var candidate = System.IO.Path.Combine(programFiles, "WindowsApps", packageFullName);
            // We can't List the folder but a DLL probe is fine — return the path and
            // let ExtractIconEx do its own File.Exists check on the joined path.
            return candidate;
        }
        return null;
    }

    private static bool LooksLikeClsid(string s)
        => s.Length >= 38 && s.StartsWith('{') && s.EndsWith('}');

    /// <summary>
    /// PackagedCom Server values can be raw text ("WinRAR") or MUI indirect strings
    /// ("@{Pkg?ms-resource://...}"). Pass them through SHLoadIndirectString when an
    /// IMuiStringResolver is supplied; otherwise return the raw value.
    /// </summary>
    private string? ResolveString(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        if (raw[0] != '@' || _mui == null) return raw;
        return _mui.Resolve(raw) ?? raw;
    }
}
