using System.Collections.Generic;
using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class CascadeProtectionServiceTests
{
    private static PackagedShellExt BackgroundExt(
        string clsid,
        string aumid,
        string displayName,
        string? publisherDisplayName = null)
        => new PackagedShellExt
        {
            Clsid = clsid,
            PackageFullName = displayName + "_1.0_x64__abcdef",
            DisplayName = displayName,
            PublisherDisplayName = publisherDisplayName ?? displayName,
            ItemTypes = new[] { "Directory", "Directory\\Background" },
            Aumid = aumid,
        };

    [Fact]
    public void PlanProtections_returns_empty_when_no_other_background_extensions_exist()
    {
        var reg = new FakeRegistry();
        var sut = new CascadeProtectionService(reg);
        var amd = BackgroundExt("{6767B3BC-8FF7-11EC-B909-0242AC120002}", "Amd_pubhash!App", "AMD");

        var plans = sut.PlanProtections(amd.Clsid, new[] { amd });

        Assert.Empty(plans);
    }

    [Fact]
    public void PlanProtections_targets_other_background_extensions_in_both_scopes()
    {
        var reg = new FakeRegistry();
        var sut = new CascadeProtectionService(reg);
        var amd = BackgroundExt("{6767B3BC-8FF7-11EC-B909-0242AC120002}", "Amd_pubhash!App", "AMD");
        var term = BackgroundExt("{9F156763-7844-4DC4-B2B1-901F640F5155}", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "Terminal");

        var plans = sut.PlanProtections(amd.Clsid, new[] { amd, term });

        // Expect one plan per scope for Terminal (the OTHER background ext).
        Assert.Equal(2, plans.Count);
        Assert.All(plans, p => Assert.Equal(term.Clsid, p.SourceClsid));
        Assert.Contains(plans, p => p.Scope == "Directory");
        Assert.Contains(plans, p => p.Scope == "Directory\\Background");
        // Verb name uses the source CLSID without braces.
        Assert.All(plans, p => Assert.Contains("9F156763", p.VerbPath));
        // Command launches via AppsFolder + AUMID.
        Assert.All(plans, p => Assert.Equal("explorer.exe shell:AppsFolder\\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", p.Command));
    }

    [Fact]
    public void PlanProtections_skips_extensions_without_aumid()
    {
        var reg = new FakeRegistry();
        var sut = new CascadeProtectionService(reg);
        var amd = BackgroundExt("{6767B3BC-8FF7-11EC-B909-0242AC120002}", "Amd_pubhash!App", "AMD");
        var orphan = new PackagedShellExt
        {
            Clsid = "{11111111-1111-1111-1111-111111111111}",
            PackageFullName = "Orphan_1_x64__none",
            DisplayName = "Orphan",
            PublisherDisplayName = "Orphan",
            ItemTypes = new[] { "Directory\\Background" },
            Aumid = null,
        };

        var plans = sut.PlanProtections(amd.Clsid, new[] { amd, orphan });

        Assert.Empty(plans);
    }

    [Fact]
    public void PlanProtections_skips_non_background_extensions()
    {
        var reg = new FakeRegistry();
        var sut = new CascadeProtectionService(reg);
        var amd = BackgroundExt("{6767B3BC-8FF7-11EC-B909-0242AC120002}", "Amd_pubhash!App", "AMD");
        var nonBg = new PackagedShellExt
        {
            Clsid = "{22222222-2222-2222-2222-222222222222}",
            PackageFullName = "FileOnly_1_x64__none",
            DisplayName = "FileOnly",
            PublisherDisplayName = "FileOnly",
            ItemTypes = new[] { "*" },  // files only, no Background
            Aumid = "FileOnly_pub!App",
        };

        var plans = sut.PlanProtections(amd.Clsid, new[] { amd, nonBg });

        Assert.Empty(plans);
    }

    [Fact]
    public void PlanProtections_is_idempotent_against_existing_protections()
    {
        var reg = new FakeRegistry();
        var sut = new CascadeProtectionService(reg);
        var amd = BackgroundExt("{6767B3BC-8FF7-11EC-B909-0242AC120002}", "Amd_pubhash!App", "AMD");
        var term = BackgroundExt("{9F156763-7844-4DC4-B2B1-901F640F5155}", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "Terminal");

        // First plan + install.
        var first = sut.PlanProtections(amd.Clsid, new[] { amd, term });
        sut.Install(first);

        // Second plan against same input should find nothing new to do.
        var second = sut.PlanProtections(amd.Clsid, new[] { amd, term });

        Assert.NotEmpty(first);
        Assert.Empty(second);
    }

    [Fact]
    public void Install_writes_classic_verb_with_display_icon_and_command()
    {
        var reg = new FakeRegistry();
        var sut = new CascadeProtectionService(reg);
        var term = new PackagedShellExt
        {
            Clsid = "{9F156763-7844-4DC4-B2B1-901F640F5155}",
            PackageFullName = "Microsoft.WindowsTerminal_1.24.10921.0_x64__8wekyb3d8bbwe",
            DisplayName = "WindowsTerminalShellExt",  // raw class name — must NOT win
            PublisherDisplayName = "Terminal",         // friendly — should be used
            LogoPath = @"C:\Logo.png",
            ItemTypes = new[] { "Directory\\Background" },
            Aumid = "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
        };
        var amd = BackgroundExt("{6767B3BC-8FF7-11EC-B909-0242AC120002}", "Amd_pubhash!App", "AMD");

        var plans = sut.PlanProtections(amd.Clsid, new[] { amd, term });
        sut.Install(plans);

        // Verb path uses CLSID with braces stripped, retaining the inner dashes.
        var stripped = "Software\\Classes\\Directory\\Background\\shell\\RcmmProtect_" + "9F156763-7844-4DC4-B2B1-901F640F5155";
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, stripped));
        // Friendly publisher name, not the technical class DisplayName.
        Assert.Equal("Terminal", reg.GetValue(RegistryHive.CurrentUser, stripped, ""));
        Assert.Equal(@"C:\Logo.png", reg.GetValue(RegistryHive.CurrentUser, stripped, "Icon"));
        Assert.Equal("", reg.GetValue(RegistryHive.CurrentUser, stripped, "NoWorkingDirectory"));
        var cmd = reg.GetValue(RegistryHive.CurrentUser, stripped + "\\command", "");
        Assert.Equal("explorer.exe shell:AppsFolder\\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", cmd);
    }

    [Fact]
    public void Install_omits_icon_value_when_logo_path_unresolved()
    {
        var reg = new FakeRegistry();
        var sut = new CascadeProtectionService(reg);
        var term = new PackagedShellExt
        {
            Clsid = "{9F156763-7844-4DC4-B2B1-901F640F5155}",
            PackageFullName = "Microsoft.WindowsTerminal_1.24.10921.0_x64__8wekyb3d8bbwe",
            DisplayName = "WindowsTerminalShellExt",
            PublisherDisplayName = "Terminal",
            DllPath = @"C:\Some\Package.dll",  // dll without icon resources — must NOT become Icon
            LogoPath = null,
            ItemTypes = new[] { "Directory\\Background" },
            Aumid = "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
        };
        var amd = BackgroundExt("{6767B3BC-8FF7-11EC-B909-0242AC120002}", "Amd_pubhash!App", "AMD");

        sut.Install(sut.PlanProtections(amd.Clsid, new[] { amd, term }));

        var path = "Software\\Classes\\Directory\\Background\\shell\\RcmmProtect_9F156763-7844-4DC4-B2B1-901F640F5155";
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, path));
        // No Icon written — Explorer picks a default rather than rendering a broken iconless menu item.
        Assert.Null(reg.GetValue(RegistryHive.CurrentUser, path, "Icon"));
    }

    [Fact]
    public void PlanProtections_returns_empty_when_legacy_menu_hack_active()
    {
        var reg = new FakeRegistry();
        // The presence of the InprocServer32 key alone is what Explorer keys
        // off — even with an empty (default) value.
        reg.CreateKey(RegistryHive.CurrentUser, CascadeProtectionService.LegacyMenuHackKey);
        reg.SetValue(RegistryHive.CurrentUser, CascadeProtectionService.LegacyMenuHackKey, "", "");
        var sut = new CascadeProtectionService(reg);

        var amd = BackgroundExt("{6767B3BC-8FF7-11EC-B909-0242AC120002}", "Amd_pubhash!App", "AMD");
        var term = BackgroundExt("{9F156763-7844-4DC4-B2B1-901F640F5155}", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "Terminal");

        var plans = sut.PlanProtections(amd.Clsid, new[] { amd, term });

        Assert.Empty(plans);
    }

    [Fact]
    public void UninstallAll_removes_only_RcmmProtect_prefixed_verbs()
    {
        var reg = new FakeRegistry();
        var sut = new CascadeProtectionService(reg);

        // User-authored verb (Open in Terminal) — must survive UninstallAll.
        var userVerb = "Software\\Classes\\Directory\\Background\\shell\\OpenInTerminal";
        reg.SetValue(RegistryHive.CurrentUser, userVerb, "", "Open in &Terminal");
        reg.SetValue(RegistryHive.CurrentUser, userVerb + "\\command", "", @"wt.exe -d ""%V""");

        // RCMM-installed protection verb.
        var amd = BackgroundExt("{6767B3BC-8FF7-11EC-B909-0242AC120002}", "Amd_pubhash!App", "AMD");
        var term = BackgroundExt("{9F156763-7844-4DC4-B2B1-901F640F5155}", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "Terminal");
        sut.Install(sut.PlanProtections(amd.Clsid, new[] { amd, term }));

        var removed = sut.UninstallAll();

        Assert.True(removed >= 2); // two scopes for Terminal
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, userVerb)); // user-authored verb untouched
        // No RCMM-prefixed verbs survive.
        Assert.DoesNotContain(
            reg.GetSubKeyNames(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\shell"),
            n => n.StartsWith(CascadeProtectionService.VerbPrefix));
    }
}
