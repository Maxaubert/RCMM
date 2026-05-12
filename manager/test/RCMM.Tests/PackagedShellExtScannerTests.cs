using System.Linq;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class PackagedShellExtScannerTests
{
    private const string PkgRoot = @"SOFTWARE\Classes\PackagedCom\Package";

    [Fact]
    public void Scan_returns_empty_when_packagedcom_root_missing()
    {
        var reg = new FakeRegistry();
        var sut = new PackagedShellExtScanner(reg);
        Assert.Empty(sut.Scan());
    }

    [Fact]
    public void Scan_returns_surrogate_classes_with_displayname_and_publisher()
    {
        var reg = new FakeRegistry();
        // WinRAR-shape server entry.
        var pkg = "WinRAR.ShellExtension_1.0.0.2_x64__d9ma7nkbkv4rp";
        var server = $@"{PkgRoot}\{pkg}\Server\0";
        reg.SetValue(RegistryHive.LocalMachine, server, "ApplicationDisplayName", "WinRAR");
        reg.SetValue(RegistryHive.LocalMachine, server, "DisplayName", "WinRAR");
        reg.SetValue(RegistryHive.LocalMachine, server, "SurrogateAppId",
            "{B41DB860-64E4-11D2-9906-E49FADC173CA}");

        var sut = new PackagedShellExtScanner(reg);
        var items = sut.Scan().ToList();

        Assert.Single(items);
        Assert.Equal("{B41DB860-64E4-11D2-9906-E49FADC173CA}", items[0].Clsid);
        Assert.Equal("WinRAR", items[0].DisplayName);
        Assert.Equal("WinRAR", items[0].PublisherDisplayName);
    }

    [Fact]
    public void Scan_skips_servers_with_executable_field()
    {
        var reg = new FakeRegistry();
        var pkg = "App.Toast_1_x64__abc";
        var server = $@"{PkgRoot}\{pkg}\Server\0";
        reg.SetValue(RegistryHive.LocalMachine, server, "Executable", "app.exe");
        reg.SetValue(RegistryHive.LocalMachine, server, "ApplicationDisplayName", "App");
        reg.SetValue(RegistryHive.LocalMachine, server, "DisplayName", "Toast activator");
        // Even with SurrogateAppId present, presence of Executable disqualifies the entry.
        reg.SetValue(RegistryHive.LocalMachine, server, "SurrogateAppId",
            "{11111111-2222-3333-4444-555555555555}");

        var sut = new PackagedShellExtScanner(reg);
        Assert.Empty(sut.Scan());
    }

    [Fact]
    public void Scan_skips_servers_without_surrogateappid()
    {
        var reg = new FakeRegistry();
        var server = $@"{PkgRoot}\Pkg.Foo_1_x64__abc\Server\0";
        reg.SetValue(RegistryHive.LocalMachine, server, "DisplayName", "no surrogate");
        // No SurrogateAppId, no Executable — still skipped (not a COM-surrogate context menu).

        var sut = new PackagedShellExtScanner(reg);
        Assert.Empty(sut.Scan());
    }

    [Fact]
    public void Scan_normalizes_clsid_to_uppercase()
    {
        var reg = new FakeRegistry();
        var server = $@"{PkgRoot}\Pkg.Foo_1_x64__abc\Server\0";
        reg.SetValue(RegistryHive.LocalMachine, server, "ApplicationDisplayName", "Foo");
        reg.SetValue(RegistryHive.LocalMachine, server, "DisplayName", "Foo");
        reg.SetValue(RegistryHive.LocalMachine, server, "SurrogateAppId",
            "{aaaabbbb-1111-2222-3333-444455556666}");

        var sut = new PackagedShellExtScanner(reg);
        var items = sut.Scan().ToList();

        Assert.Single(items);
        Assert.Equal("{AAAABBBB-1111-2222-3333-444455556666}", items[0].Clsid);
    }
}
