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
    public void PlanProtections_targets_other_background_extensions_only_in_background_scope()
    {
        var reg = new FakeRegistry();
        var sut = new CascadeProtectionService(reg);
        var amd = BackgroundExt("{6767B3BC-8FF7-11EC-B909-0242AC120002}", "Amd_pubhash!App", "AMD");
        var term = BackgroundExt("{9F156763-7844-4DC4-B2B1-901F640F5155}", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "Terminal");

        var plans = sut.PlanProtections(amd.Clsid, new[] { amd, term });

        // Only Directory\Background is protectable — the cascade lives in the
        // modern flyout, which is the Background scope. Protecting the
        // folder-as-item Directory scope produced a duplicate menu row.
        Assert.Single(plans);
        Assert.Equal(term.Clsid, plans[0].SourceClsid);
        Assert.Equal("Directory\\Background", plans[0].Scope);
        Assert.Contains("9F156763", plans[0].VerbPath);
        Assert.Equal("explorer.exe shell:AppsFolder\\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", plans[0].Command);
    }

    [Fact]
    public void PurgeStaleDirectoryScopeProtections_removes_only_RcmmProtect_under_Directory_shell()
    {
        var reg = new FakeRegistry();
        // Legacy duplicate from a prior build (Directory scope).
        reg.CreateKey(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\shell\\RcmmProtect_DEAD");
        reg.CreateKey(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\shell\\RcmmProtect_BEEF");
        // User-authored verb under the same scope — must survive.
        reg.CreateKey(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\shell\\MyCustomVerb");
        // Background-scope protection (still legitimate) — must survive.
        reg.CreateKey(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\shell\\RcmmProtect_CAFE");

        var sut = new CascadeProtectionService(reg);
        var removed = sut.PurgeStaleDirectoryScopeProtections();

        Assert.Equal(2, removed);
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\shell\\RcmmProtect_DEAD"));
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\shell\\RcmmProtect_BEEF"));
        Assert.True (reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\shell\\MyCustomVerb"));
        Assert.True (reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\shell\\RcmmProtect_CAFE"));
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
    public void PlanProtections_skips_extensions_already_in_the_blocked_list()
    {
        var reg = new FakeRegistry();
        var sut = new CascadeProtectionService(reg);
        var amd = BackgroundExt("{6767B3BC-8FF7-11EC-B909-0242AC120002}", "Amd_pubhash!App", "AMD");
        var term = BackgroundExt("{9F156763-7844-4DC4-B2B1-901F640F5155}", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "Terminal");
        // Terminal was hidden by an earlier Apply — its CLSID sits in HKCU Blocked.
        reg.SetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, term.Clsid, "");

        var plans = sut.PlanProtections(amd.Clsid, new[] { amd, term });

        // Planning a fallback verb for Terminal would resurrect an entry the
        // user explicitly hid.
        Assert.Empty(plans);
    }

    [Fact]
    public void PlanProtections_batch_overload_excludes_every_clsid_hidden_in_the_batch()
    {
        var reg = new FakeRegistry();
        var sut = new CascadeProtectionService(reg);
        var amd = BackgroundExt("{6767B3BC-8FF7-11EC-B909-0242AC120002}", "Amd_pubhash!App", "AMD");
        var term = BackgroundExt("{9F156763-7844-4DC4-B2B1-901F640F5155}", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "Terminal");
        var code = BackgroundExt("{33333333-3333-3333-3333-333333333333}", "Code_pubhash!App", "Code");

        var plans = sut.PlanProtections(new[] { amd.Clsid, term.Clsid }, new[] { amd, term, code });

        // Both batch members excluded; only the untouched third extension is protected.
        Assert.Single(plans);
        Assert.Equal(code.Clsid, plans[0].SourceClsid);
    }

    [Fact]
    public void RemoveProtectionsFor_removes_only_the_named_clsids_verbs()
    {
        var reg = new FakeRegistry();
        var sut = new CascadeProtectionService(reg);
        var amd = BackgroundExt("{6767B3BC-8FF7-11EC-B909-0242AC120002}", "Amd_pubhash!App", "AMD");
        var term = BackgroundExt("{9F156763-7844-4DC4-B2B1-901F640F5155}", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "Terminal");
        var code = BackgroundExt("{33333333-3333-3333-3333-333333333333}", "Code_pubhash!App", "Code");
        // Hiding AMD installed verbs for Terminal and Code.
        sut.Install(sut.PlanProtections(amd.Clsid, new[] { amd, term, code }));
        var termVerb = "Software\\Classes\\Directory\\Background\\shell\\RcmmProtect_9F156763-7844-4DC4-B2B1-901F640F5155";
        var codeVerb = "Software\\Classes\\Directory\\Background\\shell\\RcmmProtect_33333333-3333-3333-3333-333333333333";
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, termVerb));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, codeVerb));

        // Terminal is now being hidden itself — only its verb must go.
        var removed = sut.RemoveProtectionsFor(new[] { term.Clsid });

        Assert.Equal(1, removed);
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser, termVerb));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, codeVerb));
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

        // One protection per Background-scope OTHER extension.
        Assert.Equal(1, removed);
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, userVerb)); // user-authored verb untouched
        // No RCMM-prefixed verbs survive.
        Assert.DoesNotContain(
            reg.GetSubKeyNames(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\shell"),
            n => n.StartsWith(CascadeProtectionService.VerbPrefix));
    }
}
