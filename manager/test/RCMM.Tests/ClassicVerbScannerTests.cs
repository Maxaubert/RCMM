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
        Assert.Equal(@"openwithhelper.exe ""%1""", entries[0].CommandLine);
    }

    [Fact]
    public void Scan_falls_back_to_key_name_when_no_display_value_at_all()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"*\shell\runme");
        var sut = MakeSut(reg);

        // No default value and no MUIVerb: Explorer renders the key name, so the
        // scanner must too — dropping the verb made one that was pre-hidden by
        // another tool unrecoverable from RCMM (review b04, finding 34).
        var entry = sut.Scan(Scope.Files).Single();
        Assert.Equal("runme", entry.DisplayName);
        Assert.Equal(EntryKind.ShellVerb, entry.Kind);
    }

    [Fact]
    public void Scan_discovers_pre_hidden_key_name_only_verb()
    {
        var reg = new FakeRegistry();
        // A tweak script wrote LegacyDisable on a verb that has no display value.
        // The scan must still yield it — this is the only discovery path for a
        // verb the live capture can't see; hidden-state is computed downstream
        // by MainViewModel.AllTargetsHidden, not by the scanner.
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\OpenWithVSCode", "LegacyDisable", "");
        var sut = MakeSut(reg);

        var entry = sut.Scan(Scope.Folders).Single();
        Assert.Equal("OpenWithVSCode", entry.DisplayName);
    }

    [Fact]
    public void Scan_still_yields_verb_carrying_LegacyDisable()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\thing", "", "Thing");
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\thing", "LegacyDisable", "");
        var sut = MakeSut(reg);

        // Disabled verbs must stay discoverable so RCMM can offer un-hiding them.
        Assert.Equal("Thing", sut.Scan(Scope.Files).Single().DisplayName);
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

    // NB: the scanner no longer computes IsBuiltIn/Source — production always
    // classified rows in MainViewModel.ResolveSourceAndBuiltIn and discarded
    // the scanner's fields, so the heuristics (and their tests) moved to
    // RegistryHeuristics / RegistryHeuristicsTests.
}
