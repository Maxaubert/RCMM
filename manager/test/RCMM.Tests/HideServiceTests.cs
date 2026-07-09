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

    [Fact]
    public void Hide_with_HideTarget_list_applies_LegacyDisable_to_each_target()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "", "Foo");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\foo", "", "Foo");
        var sut = new HideService(reg);

        var targets = new[]
        {
            new HideTarget(HideKind.LegacyDisable, RegistryHive.ClassesRoot, @"*\shell\foo", "LegacyDisable"),
            new HideTarget(HideKind.LegacyDisable, RegistryHive.ClassesRoot, @"Directory\shell\foo", "LegacyDisable"),
        };
        sut.Hide(targets);

        Assert.Equal("", reg.GetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "LegacyDisable"));
        Assert.Equal("", reg.GetValue(RegistryHive.ClassesRoot, @"Directory\shell\foo", "LegacyDisable"));
    }

    [Fact]
    public void Unhide_with_HideTarget_list_removes_LegacyDisable_from_each_target()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "LegacyDisable", "");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\foo", "LegacyDisable", "");
        var sut = new HideService(reg);

        var targets = new[]
        {
            new HideTarget(HideKind.LegacyDisable, RegistryHive.ClassesRoot, @"*\shell\foo", "LegacyDisable"),
            new HideTarget(HideKind.LegacyDisable, RegistryHive.ClassesRoot, @"Directory\shell\foo", "LegacyDisable"),
        };
        sut.Unhide(targets);

        Assert.Null(reg.GetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "LegacyDisable"));
        Assert.Null(reg.GetValue(RegistryHive.ClassesRoot, @"Directory\shell\foo", "LegacyDisable"));
    }

    [Fact]
    public void Hide_with_HkcuMask_target_creates_mask_key()
    {
        var reg = new FakeRegistry();
        var sut = new HideService(reg);

        var targets = new[]
        {
            new HideTarget(HideKind.HkcuMask, RegistryHive.CurrentUser,
                           @"Software\Classes\*\shellex\ContextMenuHandlers\X", null),
        };
        sut.Hide(targets);

        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            @"Software\Classes\*\shellex\ContextMenuHandlers\X"));
    }

    [Fact]
    public void Unhide_with_HkcuMask_target_deletes_mask_key()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.CurrentUser, @"Software\Classes\*\shellex\ContextMenuHandlers\X");
        var sut = new HideService(reg);

        var targets = new[]
        {
            new HideTarget(HideKind.HkcuMask, RegistryHive.CurrentUser,
                           @"Software\Classes\*\shellex\ContextMenuHandlers\X", null),
        };
        sut.Unhide(targets);

        Assert.False(reg.KeyExists(RegistryHive.CurrentUser,
            @"Software\Classes\*\shellex\ContextMenuHandlers\X"));
    }

    // --- HkcuMask must not destroy a shellex whose REAL home is HKCU ---
    // (per-user installed handlers, not an HKLM original RCMM is shadowing).

    private const string RealClsid = "{11111111-2222-3333-4444-555555555555}";
    private const string PerUserPath = @"Software\Classes\*\shellex\ContextMenuHandlers\PerUserHandler";

    [Fact]
    public void Hide_HkcuMask_over_a_real_per_user_registration_stashes_the_original_and_masks()
    {
        var reg = new FakeRegistry();
        // A genuine per-user shellex: the HKCU key already carries a real CLSID.
        reg.SetValue(RegistryHive.CurrentUser, PerUserPath, "", RealClsid);
        var sut = new HideService(reg);

        sut.Hide(new[] { new HideTarget(HideKind.HkcuMask, RegistryHive.CurrentUser, PerUserPath, null) });

        // Masked (default emptied) but the original CLSID preserved for restore.
        Assert.Equal("", reg.GetValue(RegistryHive.CurrentUser, PerUserPath, ""));
        Assert.Equal(RealClsid, reg.GetValue(RegistryHive.CurrentUser, PerUserPath, HideService.SavedDefaultValueName));
    }

    [Fact]
    public void Unhide_HkcuMask_restores_the_stashed_registration_instead_of_deleting_it()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.CurrentUser, PerUserPath, "", RealClsid);
        var sut = new HideService(reg);

        sut.Hide(new[] { new HideTarget(HideKind.HkcuMask, RegistryHive.CurrentUser, PerUserPath, null) });
        sut.Unhide(new[] { new HideTarget(HideKind.HkcuMask, RegistryHive.CurrentUser, PerUserPath, null) });

        // Fully reversible: the real registration is back, the stash is gone, the key survives.
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, PerUserPath));
        Assert.Equal(RealClsid, reg.GetValue(RegistryHive.CurrentUser, PerUserPath, ""));
        Assert.Null(reg.GetValue(RegistryHive.CurrentUser, PerUserPath, HideService.SavedDefaultValueName));
    }

    [Fact]
    public void Unhide_HkcuMask_does_not_delete_a_key_that_still_holds_a_real_registration()
    {
        // A per-user handler that RCMM never masked (e.g. falsely shown as hidden).
        // Unhide must not wipe it: there is nothing of ours to remove.
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.CurrentUser, PerUserPath, "", RealClsid);
        var sut = new HideService(reg);

        sut.Unhide(new[] { new HideTarget(HideKind.HkcuMask, RegistryHive.CurrentUser, PerUserPath, null) });

        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, PerUserPath));
        Assert.Equal(RealClsid, reg.GetValue(RegistryHive.CurrentUser, PerUserPath, ""));
    }

    [Fact]
    public void RequiresExplorerRestart_list_overload_is_true_when_any_target_is_HkcuMask()
    {
        var verb = new HideTarget(HideKind.LegacyDisable, RegistryHive.ClassesRoot, "p", "v");
        var mask = new HideTarget(HideKind.HkcuMask, RegistryHive.CurrentUser, "p", null);
        Assert.False(HideService.RequiresExplorerRestart(new[] { verb }));
        Assert.True(HideService.RequiresExplorerRestart(new[] { verb, mask }));
    }

    [Fact]
    public void Hide_BlockedShellExt_writes_clsid_value_under_blocked_list()
    {
        var reg = new FakeRegistry();
        var sut = new HideService(reg);
        var target = HideService.BlockedShellExtTarget("{B41DB860-64E4-11D2-9906-E49FADC173CA}");

        sut.Hide(new[] { target });

        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, HideService.BlockedListPath));
        Assert.Equal("", reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath,
            "{B41DB860-64E4-11D2-9906-E49FADC173CA}"));
    }

    [Fact]
    public void Unhide_BlockedShellExt_removes_clsid_value()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.CurrentUser, HideService.BlockedListPath,
            "{B41DB860-64E4-11D2-9906-E49FADC173CA}", "");
        var sut = new HideService(reg);
        var target = HideService.BlockedShellExtTarget("{B41DB860-64E4-11D2-9906-E49FADC173CA}");

        sut.Unhide(new[] { target });

        Assert.Null(reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath,
            "{B41DB860-64E4-11D2-9906-E49FADC173CA}"));
    }

    [Fact]
    public void RequiresExplorerRestart_list_overload_is_true_for_BlockedShellExt()
    {
        var blocked = HideService.BlockedShellExtTarget("{B41DB860-64E4-11D2-9906-E49FADC173CA}");
        Assert.True(HideService.RequiresExplorerRestart(new[] { blocked }));
    }
}
