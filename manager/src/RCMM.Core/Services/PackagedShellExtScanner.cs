using System;
using System.Collections.Generic;
using System.Linq;
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
            // ItemType map (CLSID → set of ItemType strings) and AUMID are derived from
            // the AppXManifest once per package. Packaged-COM context menu extensions
            // declare their target scope (Directory, Directory\Background, *) under
            // <windows.fileExplorerContextMenus> and are referenced there by CLSID; we
            // need this mapping so cascade-protection knows which CLSIDs share scope
            // with each other (see CLAUDE.md "packaged-COM Directory\Background cascade").
            ManifestExtensionInfo? mfst = installFolder != null ? ResolveManifestInfo(installFolder, pkg) : null;

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

                IReadOnlyList<string> itemTypes = Array.Empty<string>();
                if (mfst != null && mfst.ItemTypesByClsid.TryGetValue(clsid!.Trim().ToUpperInvariant(), out var its))
                    itemTypes = its;

                yielded++;
                yield return new PackagedShellExt
                {
                    Clsid = clsid.Trim().ToUpperInvariant(),
                    PackageFullName = pkg,
                    DisplayName = chosenDisplay!,
                    PublisherDisplayName = publisher!,
                    DllPath = absDll,
                    LogoPath = installFolder != null ? ResolvePackageLogo(installFolder) : null,
                    ItemTypes = itemTypes,
                    Aumid = mfst?.Aumid,
                };
            }
        }

        Log.Info(Cat, $"PackagedShellExtScanner packages={packages} candidates={yielded}");
    }

    /// <summary>
    /// AppXManifest-derived extension info: which CLSIDs target which ItemTypes in
    /// &lt;windows.fileExplorerContextMenus&gt;, and the package's AUMID (PackageFamilyName!ApplicationId).
    /// </summary>
    public sealed class ManifestExtensionInfo
    {
        public Dictionary<string, IReadOnlyList<string>> ItemTypesByClsid { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
        public string? Aumid { get; init; }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ManifestExtensionInfo?> _mfstCache
        = new(StringComparer.OrdinalIgnoreCase);

    private ManifestExtensionInfo? ResolveManifestInfo(string installFolder, string packageFullName)
        => _mfstCache.GetOrAdd(installFolder, _ =>
        {
            try
            {
                var manifest = System.IO.Path.Combine(installFolder, "AppxManifest.xml");
                if (!System.IO.File.Exists(manifest)) return null;
                var doc = new System.Xml.XmlDocument();
                doc.Load(manifest);
                return ParseManifestExtensionInfo(doc, packageFullName);
            }
            catch (Exception ex)
            {
                Log.Debug(Cat, $"manifest parse for context-menu extension info failed for {installFolder}: {ex.Message}");
                return null;
            }
        });

    /// <summary>
    /// Pure helper exposed for tests: parse an in-memory AppxManifest XmlDocument
    /// into a map of CLSID → ItemTypes plus an AUMID. The manifest mixes element
    /// names from several namespaces (desktop4:, desktop5:, foundation:) so we
    /// match by LocalName to stay robust across namespace variants.
    /// </summary>
    public static ManifestExtensionInfo ParseManifestExtensionInfo(System.Xml.XmlDocument doc, string packageFullName)
    {
        var byClsid = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // Walk the whole doc looking for any element whose LocalName is "ItemType";
        // each one has a Type attribute and contains one or more Verb children with
        // a Clsid attribute. The manifest's actual XML namespace varies (desktop4,
        // desktop5, desktop6 across builds) so LocalName matching is the only safe
        // strategy.
        var itemTypes = doc.GetElementsByTagName("*");
        foreach (System.Xml.XmlNode? n in itemTypes)
        {
            if (n is not System.Xml.XmlElement el) continue;
            if (!string.Equals(el.LocalName, "ItemType", StringComparison.Ordinal)) continue;
            var type = el.GetAttribute("Type");
            if (string.IsNullOrWhiteSpace(type)) continue;
            foreach (System.Xml.XmlNode child in el.ChildNodes)
            {
                if (child is not System.Xml.XmlElement vEl) continue;
                if (!string.Equals(vEl.LocalName, "Verb", StringComparison.Ordinal)) continue;
                var clsid = vEl.GetAttribute("Clsid");
                if (string.IsNullOrWhiteSpace(clsid)) continue;
                var key = NormalizeClsid(clsid);
                if (!byClsid.TryGetValue(key, out var list))
                {
                    list = new List<string>();
                    byClsid[key] = list;
                }
                if (!list.Contains(type, StringComparer.OrdinalIgnoreCase))
                    list.Add(type);
            }
        }

        // AUMID: PackageFamilyName + "!" + ApplicationId. PackageFamilyName is the
        // package's <Name>_<PublisherHash>; we derive it from packageFullName which
        // is formatted "<Name>_<Version>_<Arch>_<ResourceId>_<PublisherHash>".
        string? aumid = null;
        try
        {
            string? appId = null;
            foreach (System.Xml.XmlNode? app in doc.GetElementsByTagName("Application"))
            {
                if (app is not System.Xml.XmlElement aEl) continue;
                appId = aEl.GetAttribute("Id");
                if (!string.IsNullOrWhiteSpace(appId)) break;
            }
            if (!string.IsNullOrWhiteSpace(appId))
            {
                var familyName = DerivePackageFamilyName(packageFullName);
                if (!string.IsNullOrEmpty(familyName))
                    aumid = familyName + "!" + appId;
            }
        }
        catch { /* AUMID stays null — protection service falls back to launching nothing */ }

        var converted = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in byClsid) converted[kv.Key] = kv.Value;
        return new ManifestExtensionInfo { ItemTypesByClsid = converted, Aumid = aumid };
    }

    private static string NormalizeClsid(string s)
    {
        var t = s.Trim();
        if (t.Length == 0) return t;
        if (t[0] != '{') t = "{" + t;
        if (t[t.Length - 1] != '}') t = t + "}";
        return t.ToUpperInvariant();
    }

    /// <summary>
    /// PackageFamilyName from a PackageFullName: keep first segment (Name) and last
    /// segment (PublisherHash), drop version/arch/resourceId. The 8.3-style
    /// publisher hash is always at the end of the FullName. Returns null when the
    /// FullName doesn't have the expected number of components.
    /// </summary>
    public static string? DerivePackageFamilyName(string packageFullName)
    {
        if (string.IsNullOrEmpty(packageFullName)) return null;
        var parts = packageFullName.Split('_');
        if (parts.Length < 2) return null;
        var name = parts[0];
        var publisherHash = parts[parts.Length - 1];
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(publisherHash)) return null;
        return name + "_" + publisherHash;
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

    // Cached per-package: AppxManifest.xml parsing isn't free and we may see
    // multiple CLSIDs from the same package in one Scan() pass.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _logoCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads &lt;package&gt;\AppxManifest.xml, pulls the smallest declared visual
    /// element logo (Square44x44Logo or Properties/Logo as fallback), and
    /// resolves it to a real file on disk — handling AppX scale variants
    /// (".scale-100.png" / ".targetsize-44.png" appended to the declared
    /// stem). Returns null if no usable asset is found; callers fall through
    /// to the existing icon-resolution chain.
    /// </summary>
    private string? ResolvePackageLogo(string installFolder)
    {
        return _logoCache.GetOrAdd(installFolder, folder =>
        {
            try
            {
                var manifest = System.IO.Path.Combine(folder, "AppxManifest.xml");
                if (!System.IO.File.Exists(manifest)) return null;
                var doc = new System.Xml.XmlDocument();
                doc.Load(manifest);
                // Smallest first — closer to the 32-44 px icon the list shows.
                var candidates = new System.Collections.Generic.List<string>();
                foreach (System.Xml.XmlNode? app in doc.GetElementsByTagName("Application"))
                {
                    if (app is not System.Xml.XmlElement el) continue;
                    foreach (System.Xml.XmlNode ve in el.ChildNodes)
                    {
                        if (ve is not System.Xml.XmlElement veEl) continue;
                        if (!veEl.LocalName.Equals("VisualElements", StringComparison.Ordinal)) continue;
                        var sq44 = veEl.GetAttribute("Square44x44Logo");
                        var sq71 = veEl.GetAttribute("Square71x71Logo");
                        var sq150 = veEl.GetAttribute("Square150x150Logo");
                        if (!string.IsNullOrWhiteSpace(sq44)) candidates.Add(sq44);
                        if (!string.IsNullOrWhiteSpace(sq71)) candidates.Add(sq71);
                        if (!string.IsNullOrWhiteSpace(sq150)) candidates.Add(sq150);
                    }
                }
                foreach (System.Xml.XmlNode? prop in doc.GetElementsByTagName("Properties"))
                {
                    if (prop is not System.Xml.XmlElement el) continue;
                    foreach (System.Xml.XmlNode child in el.ChildNodes)
                    {
                        if (child is System.Xml.XmlElement c && c.LocalName.Equals("Logo", StringComparison.Ordinal))
                            candidates.Add(c.InnerText);
                    }
                }
                foreach (var rel in candidates)
                {
                    var resolved = ResolveAppxAsset(folder, rel);
                    if (resolved != null) return resolved;
                }
            }
            catch (Exception ex) { Log.Debug(Cat, $"manifest parse failed for {folder}: {ex.Message}"); }
            return null;
        });
    }

    private static string? ResolveAppxAsset(string installFolder, string relativePath)
    {
        // 1. Literal path — older / hand-packed manifests (PowerToys ImageResizer,
        //    Notepad++ via Maximus/MSIX) ship the asset at the declared name.
        var literal = System.IO.Path.Combine(installFolder, relativePath);
        if (System.IO.File.Exists(literal)) return literal;

        // 2. Scale variants — MakeAppx splits one logical asset into multiple
        //    physical files. Try the conventional suffixes in order of preference.
        var dir = System.IO.Path.GetDirectoryName(literal);
        if (string.IsNullOrEmpty(dir)) return null;
        var stem = System.IO.Path.GetFileNameWithoutExtension(literal);
        var ext = System.IO.Path.GetExtension(literal);
        foreach (var suffix in new[]
        {
            ".scale-100", ".scale-125", ".scale-150", ".scale-200", ".scale-400",
            ".targetsize-44", ".targetsize-32", ".targetsize-48", ".targetsize-96",
            ".scale-100_contrast-standard", ".scale-200_contrast-standard"
        })
        {
            var guess = System.IO.Path.Combine(dir, stem + suffix + ext);
            if (System.IO.File.Exists(guess)) return guess;
        }

        // 3. Fallback: if the parent is enumerable, glob for any file whose name
        //    starts with the stem. WindowsApps subfolders are usually readable.
        try
        {
            if (System.IO.Directory.Exists(dir))
            {
                var hit = System.IO.Directory.EnumerateFiles(dir, stem + "*" + ext).FirstOrDefault();
                if (hit != null) return hit;
            }
        }
        catch { }
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
