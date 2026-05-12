using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class ClassicVerbScannerTests
{
    private static ClassicVerbScanner MakeSut(FakeRegistry reg, FakeMuiStringResolver? mui = null)
        => new ClassicVerbScanner(reg, mui ?? new FakeMuiStringResolver());

    [Fact]
    public void Scan_returns_empty_when_no_shell_key()
    {
        var reg = new FakeRegistry();
        var sut = MakeSut(reg);
        Assert.Empty(sut.Scan(Scope.Files));
    }

    [Fact]
    public void Scan_finds_a_simple_verb()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\openwith", "", "Open with…");
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\openwith\command", "", @"openwithhelper.exe ""%1""");
        var sut = MakeSut(reg);

        var entries = sut.Scan(Scope.Files).ToList();
        Assert.Single(entries);
        Assert.Equal("Open with…", entries[0].DisplayName);
        Assert.Equal(EntryKind.ShellVerb, entries[0].Kind);
        Assert.Equal(Scope.Files, entries[0].Scope);
        Assert.False(entries[0].IsHidden);
        Assert.Equal(@"openwithhelper.exe ""%1""", entries[0].CommandLine);
    }

    [Fact]
    public void Scan_drops_verb_when_no_display_name()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"*\shell\runme");
        var sut = MakeSut(reg);

        // No default value and no MUIVerb => entry is dropped rather than using key name as fallback.
        Assert.Empty(sut.Scan(Scope.Files));
    }

    [Fact]
    public void Scan_detects_LegacyDisable_as_hidden()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\thing", "", "Thing");
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\thing", "LegacyDisable", "");
        var sut = MakeSut(reg);

        Assert.True(sut.Scan(Scope.Files).Single().IsHidden);
    }

    [Fact]
    public void Scan_respects_scope_root()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\fileverb", "", "FileVerb");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\folderverb", "", "FolderVerb");
        var sut = MakeSut(reg);

        Assert.Equal("FileVerb", sut.Scan(Scope.Files).Single().DisplayName);
        Assert.Equal("FolderVerb", sut.Scan(Scope.Folders).Single().DisplayName);
    }

    [Fact]
    public void Scan_resolves_MUIVerb_reference_via_resolver()
    {
        var reg = new FakeRegistry();
        var mui = new FakeMuiStringResolver();
        mui.Map["@shell32.dll,-8506"] = "Open in Terminal";
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\OpenTerminal", "MUIVerb", "@shell32.dll,-8506");
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\OpenTerminal\command", "", "wt.exe");
        var sut = MakeSut(reg, mui);

        var entries = sut.Scan(Scope.Files).ToList();
        Assert.Single(entries);
        Assert.Equal("Open in Terminal", entries[0].DisplayName);
    }

    [Fact]
    public void Scan_resolves_default_value_MUI_reference_when_no_MUIVerb()
    {
        var reg = new FakeRegistry();
        var mui = new FakeMuiStringResolver();
        mui.Map["@shell32.dll,-9999"] = "Pin to Quick access";
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\PinToQuick", "", "@shell32.dll,-9999");
        var sut = MakeSut(reg, mui);

        var entries = sut.Scan(Scope.Files).ToList();
        Assert.Single(entries);
        Assert.Equal("Pin to Quick access", entries[0].DisplayName);
    }

    [Fact]
    public void Scan_drops_entry_when_MUI_reference_cannot_be_resolved()
    {
        var reg = new FakeRegistry();
        var mui = new FakeMuiStringResolver();
        // No mapping in fake resolver → returns null → entry should be dropped.
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\Mystery", "", "@missing.dll,-1");
        var sut = MakeSut(reg, mui);

        Assert.Empty(sut.Scan(Scope.Files));
    }

    [Fact]
    public void Scan_strips_accelerator_ampersand_from_display_name()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\edit", "", "&Edit");
        var sut = MakeSut(reg);

        Assert.Equal("Edit", sut.Scan(Scope.Files).Single().DisplayName);
    }

    [Fact]
    public void Scan_preserves_doubled_ampersand_as_literal()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\randd", "", "R&&D");
        var sut = MakeSut(reg);

        Assert.Equal("R&D", sut.Scan(Scope.Files).Single().DisplayName);
    }
}
