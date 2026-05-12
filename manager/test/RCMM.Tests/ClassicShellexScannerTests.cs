using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class ClassicShellexScannerTests
{
    [Fact]
    public void Scan_returns_empty_when_no_handlers_key()
    {
        var reg = new FakeRegistry();
        var sut = new ClassicShellexScanner(reg, new ClsidResolver(reg), new FakeFileVersionReader());
        Assert.Empty(sut.Scan(Scope.Files));
    }

    [Fact]
    public void Scan_finds_handler_by_clsid_default_value()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\WinRARShell", "", "{ABC}");
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{ABC}", "", "WinRAR Shell");
        var sut = new ClassicShellexScanner(reg, new ClsidResolver(reg), new FakeFileVersionReader());

        var entries = sut.Scan(Scope.Files).ToList();
        Assert.Single(entries);
        Assert.Equal("WinRARShell", entries[0].OriginalKeyName);
        Assert.Equal("{ABC}", entries[0].Clsid);
        Assert.Equal(EntryKind.ShellExtension, entries[0].Kind);
    }

    [Fact]
    public void Scan_uses_key_name_as_clsid_if_no_default_value()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\{XYZ}");
        var sut = new ClassicShellexScanner(reg, new ClsidResolver(reg), new FakeFileVersionReader());

        Assert.Equal("{XYZ}", sut.Scan(Scope.Files).Single().Clsid);
    }

    [Fact]
    public void Scan_detects_hidden_via_HKCU_mask()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\Foo", "", "{ABC}");
        reg.SetValue(RegistryHive.CurrentUser, @"Software\Classes\*\shellex\ContextMenuHandlers\Foo", "", "");
        var sut = new ClassicShellexScanner(reg, new ClsidResolver(reg), new FakeFileVersionReader());

        Assert.True(sut.Scan(Scope.Files).Single().IsHidden);
    }
}
