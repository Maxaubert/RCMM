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
    public void Scan_uses_clsid_defaultName_when_key_name_is_clsid()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\{XYZ}");
        // Provide a DefaultName so the entry is not dropped.
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{XYZ}", "", "Some Extension");
        var sut = new ClassicShellexScanner(reg, new ClsidResolver(reg), new FakeFileVersionReader());

        var result = sut.Scan(Scope.Files).Single();
        Assert.Equal("{XYZ}", result.Clsid);
        // DefaultName "Some Extension" != key name "{XYZ}" → used as display
        Assert.Equal("Some Extension", result.DisplayName);
    }

    [Fact]
    public void Scan_drops_handler_with_no_friendly_name()
    {
        var reg = new FakeRegistry();
        // Key name is the only potential identifier; no CLSID resolved, no FileDescription, no DefaultName.
        reg.CreateKey(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\EPP");
        var sut = new ClassicShellexScanner(reg, new ClsidResolver(reg), new FakeFileVersionReader());

        // Entry should be dropped because PickDisplay returns null.
        Assert.Empty(sut.Scan(Scope.Files));
    }

    [Fact]
    public void Scan_drops_handler_when_defaultName_equals_key_name()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\MyExt", "", "{ZZZ}");
        // DefaultName exactly matches key name (case-insensitive) → treated as no meaningful name.
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{ZZZ}", "", "MyExt");
        var sut = new ClassicShellexScanner(reg, new ClsidResolver(reg), new FakeFileVersionReader());

        Assert.Empty(sut.Scan(Scope.Files));
    }

    [Fact]
    public void Scan_detects_hidden_via_HKCU_mask()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\Foo", "", "{ABC}");
        // Give the handler a resolvable name so it isn't dropped before the hidden check.
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{ABC}", "", "Foo Extension");
        reg.SetValue(RegistryHive.CurrentUser, @"Software\Classes\*\shellex\ContextMenuHandlers\Foo", "", "");
        var sut = new ClassicShellexScanner(reg, new ClsidResolver(reg), new FakeFileVersionReader());

        Assert.True(sut.Scan(Scope.Files).Single().IsHidden);
    }
}
