using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class HideServiceTests
{
    [Fact]
    public void Hide_classic_verb_sets_LegacyDisable()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "", "Foo");
        var sut = new HideService(reg);

        var entry = new ContextMenuEntry
        {
            Id = "Files/shell/foo", DisplayName = "Foo", Source = "Test",
            Scope = Scope.Files, Kind = EntryKind.ShellVerb,
            RegistryPath = @"*\shell\foo", OriginalKeyName = "foo"
        };
        sut.Hide(entry);

        Assert.Equal("", reg.GetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "LegacyDisable"));
    }

    [Fact]
    public void Unhide_classic_verb_removes_LegacyDisable()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "LegacyDisable", "");
        var sut = new HideService(reg);

        var entry = new ContextMenuEntry
        {
            Id = "Files/shell/foo", DisplayName = "Foo", Source = "Test",
            Scope = Scope.Files, Kind = EntryKind.ShellVerb,
            RegistryPath = @"*\shell\foo", OriginalKeyName = "foo"
        };
        sut.Unhide(entry);

        Assert.Null(reg.GetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "LegacyDisable"));
    }

    [Fact]
    public void Hide_classic_shellex_creates_HKCU_mask_key()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\WinRAR", "", "{X}");
        var sut = new HideService(reg);

        var entry = new ContextMenuEntry
        {
            Id = "Files/shellex/WinRAR", DisplayName = "WinRAR", Source = "Test",
            Scope = Scope.Files, Kind = EntryKind.ShellExtension,
            RegistryPath = @"*\shellex\ContextMenuHandlers\WinRAR", OriginalKeyName = "WinRAR",
            Clsid = "{X}"
        };
        sut.Hide(entry);

        Assert.True(reg.KeyExists(
            RegistryHive.CurrentUser,
            @"Software\Classes\*\shellex\ContextMenuHandlers\WinRAR"));
    }

    [Fact]
    public void Unhide_classic_shellex_removes_HKCU_mask_key()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.CurrentUser, @"Software\Classes\*\shellex\ContextMenuHandlers\WinRAR");
        var sut = new HideService(reg);

        var entry = new ContextMenuEntry
        {
            Id = "Files/shellex/WinRAR", DisplayName = "WinRAR", Source = "Test",
            Scope = Scope.Files, Kind = EntryKind.ShellExtension,
            RegistryPath = @"*\shellex\ContextMenuHandlers\WinRAR", OriginalKeyName = "WinRAR",
            Clsid = "{X}"
        };
        sut.Unhide(entry);

        Assert.False(reg.KeyExists(
            RegistryHive.CurrentUser,
            @"Software\Classes\*\shellex\ContextMenuHandlers\WinRAR"));
    }

    [Theory]
    [InlineData(EntryKind.ShellVerb, false)]
    [InlineData(EntryKind.ShellExtension, true)]
    public void RequiresExplorerRestart_only_for_shell_extensions(EntryKind kind, bool expected)
    {
        Assert.Equal(expected, HideService.RequiresExplorerRestart(kind));
    }
}
