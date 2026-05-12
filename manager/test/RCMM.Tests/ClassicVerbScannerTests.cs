using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class ClassicVerbScannerTests
{
    [Fact]
    public void Scan_returns_empty_when_no_shell_key()
    {
        var reg = new FakeRegistry();
        var sut = new ClassicVerbScanner(reg);
        Assert.Empty(sut.Scan(Scope.Files));
    }

    [Fact]
    public void Scan_finds_a_simple_verb()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\openwith", "", "Open with…");
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\openwith\command", "", @"openwithhelper.exe ""%1""");
        var sut = new ClassicVerbScanner(reg);

        var entries = sut.Scan(Scope.Files).ToList();
        Assert.Single(entries);
        Assert.Equal("Open with…", entries[0].DisplayName);
        Assert.Equal(EntryKind.ShellVerb, entries[0].Kind);
        Assert.Equal(Scope.Files, entries[0].Scope);
        Assert.False(entries[0].IsHidden);
        Assert.Equal(@"openwithhelper.exe ""%1""", entries[0].CommandLine);
    }

    [Fact]
    public void Scan_falls_back_to_key_name_when_no_display_name()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"*\shell\runme");
        var sut = new ClassicVerbScanner(reg);

        var entries = sut.Scan(Scope.Files).ToList();
        Assert.Equal("runme", entries[0].DisplayName);
    }

    [Fact]
    public void Scan_detects_LegacyDisable_as_hidden()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\thing", "", "Thing");
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\thing", "LegacyDisable", "");
        var sut = new ClassicVerbScanner(reg);

        Assert.True(sut.Scan(Scope.Files).Single().IsHidden);
    }

    [Fact]
    public void Scan_respects_scope_root()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\fileverb", "", "FileVerb");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\folderverb", "", "FolderVerb");
        var sut = new ClassicVerbScanner(reg);

        Assert.Equal("FileVerb", sut.Scan(Scope.Files).Single().DisplayName);
        Assert.Equal("FolderVerb", sut.Scan(Scope.Folders).Single().DisplayName);
    }
}
