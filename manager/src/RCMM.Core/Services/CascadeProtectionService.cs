using System;
using System.Collections.Generic;
using System.Linq;
using RCMM.Core.Diagnostics;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

/// <summary>
/// Defends against the Windows 11 modern-menu cascade where adding any
/// `Directory\Background`-scoped packaged-COM CLSID to
/// HKCU\…\Shell Extensions\Blocked can also knock OTHER packaged-COM
/// Background extensions out of the flyout (see CLAUDE.md "packaged-COM
/// Directory\Background cascade" — observed: hiding AMD Radeon Software
/// silently removed "Open in Terminal"). Each protected extension gets a
/// classic-verb fallback under HKCU\Software\Classes\Directory(\Background)
/// \shell\<verb> so its row stays visible in the legacy/classic menu and
/// in RCMM's IContextMenu probe, independent of packaged-COM activation.
///
/// Verbs created here are namespaced "RcmmProtect_&lt;CLSID-without-braces&gt;"
/// so we can tell our own writes apart from user-authored verbs at unhide
/// time and only remove what we own.
/// </summary>
public sealed class CascadeProtectionService
{
    public const string VerbPrefix = "RcmmProtect_";
    /// <summary>
    /// HKCU CLSID key that, when present (even with an empty
    /// InprocServer32\(default)), forces Explorer to use the classic
    /// IContextMenu menu instead of the modern flyout. When this is set,
    /// the cascade we're protecting against can't trigger (it lives in
    /// the modern-menu IExplorerCommand enumeration), so protection
    /// installation is skipped — adding the verbs anyway would just
    /// clutter the user's classic menu with raw packaged-COM placeholders.
    /// </summary>
    public const string LegacyMenuHackKey =
        "Software\\Classes\\CLSID\\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\\InprocServer32";
    private const string Cat = "cascade";

    private readonly IRegistry _reg;

    public CascadeProtectionService(IRegistry reg) { _reg = reg; }

    /// <summary>
    /// True when the Windows 11 legacy context-menu hack is active for the
    /// current user. Callers use this to short-circuit Install: the cascade
    /// we're protecting against only manifests in the modern menu, which the
    /// hack disables.
    /// </summary>
    public bool IsLegacyMenuModeActive()
        => _reg.KeyExists(RegistryHive.CurrentUser, LegacyMenuHackKey);

    /// <summary>
    /// For a given CLSID being hidden, enumerate the OTHER Background-scope
    /// packaged-COM extensions that should get classic-verb fallbacks so the
    /// cascade can't make them invisible. Returns the protection plans the
    /// caller should apply via Install. Idempotent against an existing
    /// protection: rows whose protection verb already exists in HKCU are
    /// skipped. Returns empty when the legacy menu hack is active (the
    /// cascade can't trigger in classic-menu mode).
    /// </summary>
    public IReadOnlyList<ProtectionPlan> PlanProtections(
        string clsidBeingHidden,
        IEnumerable<PackagedShellExt> allExtensions)
    {
        var plans = new List<ProtectionPlan>();
        var byClsidHidden = clsidBeingHidden?.Trim();
        if (string.IsNullOrEmpty(byClsidHidden)) return plans;
        if (IsLegacyMenuModeActive()) return plans;

        foreach (var ext in allExtensions)
        {
            if (!ext.IsBackgroundExtension) continue;
            if (string.Equals(ext.Clsid, byClsidHidden, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(ext.Aumid)) continue;

            // Prefer the package's PublisherDisplayName ("AMD Software",
            // "Terminal", "Visual Studio Code") over the technical class
            // DisplayName ("Catalyst Context Menu extension",
            // "WindowsTerminalShellExt"). The classic verb's (default) value
            // is what Explorer renders in the menu — a raw "WindowsTerminalShellExt"
            // placeholder is exactly the look we got bitten by on the first
            // iteration. Fall back to DisplayName only if PublisherDisplayName
            // collapses to the package name.
            var friendlyName = !string.IsNullOrWhiteSpace(ext.PublisherDisplayName)
                                 && !string.Equals(ext.PublisherDisplayName, ext.PackageFullName, StringComparison.OrdinalIgnoreCase)
                ? ext.PublisherDisplayName
                : ext.DisplayName;
            // Only emit an Icon when LogoPath resolved to a real file. DllPath
            // pointing into WindowsApps often has zero icon resources (the
            // package expects Windows to render its AppX logo), so falling back
            // to it produces an iconless menu item — better to leave the Icon
            // unset and let Explorer pick a default than to point at a DLL with
            // no extractable resources.
            string? iconPath = !string.IsNullOrEmpty(ext.LogoPath) ? ext.LogoPath : null;

            // For each scope the extension declares, plan a classic-verb stub.
            foreach (var scope in ext.ItemTypes)
            {
                if (!IsProtectableScope(scope)) continue;
                var verbName = VerbPrefix + StripBraces(ext.Clsid);
                var verbPath = $"Software\\Classes\\{scope}\\shell\\{verbName}";
                if (_reg.KeyExists(RegistryHive.CurrentUser, verbPath)) continue; // already protected

                plans.Add(new ProtectionPlan(
                    scope,
                    verbPath,
                    friendlyName,
                    iconPath,
                    BuildCommand(ext.Aumid!),
                    ext.Clsid));
            }
        }
        return plans;
    }

    /// <summary>
    /// Write the classic-verb fallback registry entries described by plans.
    /// Safe to call repeatedly; existing keys are not overwritten because
    /// PlanProtections skips already-protected scopes.
    /// </summary>
    public void Install(IReadOnlyList<ProtectionPlan> plans)
    {
        foreach (var p in plans)
        {
            Log.Info(Cat, $"installing protection scope={p.Scope} clsid={p.SourceClsid} verbPath={p.VerbPath}");
            _reg.CreateKey(RegistryHive.CurrentUser, p.VerbPath);
            _reg.SetValue(RegistryHive.CurrentUser, p.VerbPath, "", p.DisplayName);
            if (!string.IsNullOrEmpty(p.IconPath))
                _reg.SetValue(RegistryHive.CurrentUser, p.VerbPath, "Icon", p.IconPath!);
            _reg.SetValue(RegistryHive.CurrentUser, p.VerbPath, "NoWorkingDirectory", "");
            _reg.CreateKey(RegistryHive.CurrentUser, p.VerbPath + "\\command");
            _reg.SetValue(RegistryHive.CurrentUser, p.VerbPath + "\\command", "", p.Command);
        }
    }

    /// <summary>
    /// Remove all RCMM-owned protection verbs (those whose subkey name starts
    /// with "RcmmProtect_") across the standard Directory and Directory\Background
    /// scopes. Called when no Background packaged-COM extension remains hidden,
    /// so the surface area stays minimal. Won't touch user-authored verbs because
    /// the prefix is RCMM-specific.
    /// </summary>
    public int UninstallAll()
    {
        int removed = 0;
        foreach (var scope in new[] { "Directory\\Background", "Directory" })
        {
            var shellPath = $"Software\\Classes\\{scope}\\shell";
            if (!_reg.KeyExists(RegistryHive.CurrentUser, shellPath)) continue;
            foreach (var name in _reg.GetSubKeyNames(RegistryHive.CurrentUser, shellPath).ToList())
            {
                if (!name.StartsWith(VerbPrefix, StringComparison.Ordinal)) continue;
                var verbPath = shellPath + "\\" + name;
                Log.Info(Cat, $"removing protection verb={verbPath}");
                _reg.DeleteKey(RegistryHive.CurrentUser, verbPath);
                removed++;
            }
        }
        return removed;
    }

    private static bool IsProtectableScope(string scope)
        => string.Equals(scope, "Directory\\Background", StringComparison.OrdinalIgnoreCase)
        || string.Equals(scope, "Directory", StringComparison.OrdinalIgnoreCase);

    private static string StripBraces(string clsid)
        => clsid.Replace("{", "").Replace("}", "");

    private static string BuildCommand(string aumid)
        => "explorer.exe shell:AppsFolder\\" + aumid;
}

/// <summary>
/// A single classic-verb fallback to install. Scope is the HKCR ItemType
/// (Directory or Directory\Background). VerbPath is the HKCU subkey path
/// relative to the hive root. SourceClsid identifies which packaged-COM
/// extension this protection mirrors — kept so logs and future diagnostics
/// can correlate verb keys to packages.
/// </summary>
public sealed record ProtectionPlan(
    string Scope,
    string VerbPath,
    string DisplayName,
    string? IconPath,
    string Command,
    string SourceClsid);
