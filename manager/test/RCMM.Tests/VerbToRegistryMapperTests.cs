using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class VerbToRegistryMapperTests
{
    [Fact]
    public void Map_verb_finds_no_targets_when_unregistered()
    {
        var reg = new FakeRegistry();
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapVerb("nonexistent").ToList();

        Assert.Empty(targets);
    }

    [Fact]
    public void MapClsid_still_finds_target_when_HKCU_mask_shadows_HKCR()
    {
        // FakeRegistry models HKCR by reading HKLM\Software\Classes — but to
        // exercise the HKLM fallback path we set HKLM directly and HKCR
        // separately to mimic the merged view: HKCR shows the HKCU shadow's
        // empty default, while the underlying HKLM key still carries the CLSID.
        var reg = new FakeRegistry();
        var clsid = "{F81E9010-6EA4-11CE-A7FF-00AA003CA9F6}";
        // HKLM original registration
        reg.SetValue(RegistryHive.LocalMachine,
            "Software\\Classes\\Directory\\shellex\\ContextMenuHandlers\\Sharing", "", clsid);
        // HKCR shows the HKCU mask's empty value (simulated)
        reg.SetValue(RegistryHive.ClassesRoot,
            "Directory\\shellex\\ContextMenuHandlers\\Sharing", "", "");

        var sut = new VerbToRegistryMapper(reg);
        var targets = sut.MapClsid(clsid).ToList();

        Assert.Single(targets);
        Assert.Equal(HideKind.HkcuMask, targets[0].Kind);
        Assert.Equal("Software\\Classes\\Directory\\shellex\\ContextMenuHandlers\\Sharing", targets[0].Path);
    }

    [Fact]
    public void Map_verb_finds_single_registry_location()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\git_shell", "", "Open Git Bash here");
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapVerb("git_shell").ToList();

        Assert.Single(targets);
        Assert.Equal(HideKind.LegacyDisable, targets[0].Kind);
        // Writes go to per-user HKCU\Software\Classes; the HKCR discovery key
        // is converted to its per-user shadow so Apply doesn't need admin.
        Assert.Equal(RegistryHive.CurrentUser, targets[0].Hive);
        Assert.Equal(@"Software\Classes\*\shell\git_shell", targets[0].Path);
        Assert.Equal("LegacyDisable", targets[0].ValueName);
    }

    [Fact]
    public void Map_verb_finds_all_scope_root_locations()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\open", "", "Open");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\open", "", "Open");
        reg.SetValue(RegistryHive.ClassesRoot, @"Drive\shell\open", "", "Open");
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapVerb("open").ToList();

        Assert.Equal(3, targets.Count);
        Assert.All(targets, t => Assert.Equal(RegistryHive.CurrentUser, t.Hive));
        Assert.Contains(targets, t => t.Path == @"Software\Classes\*\shell\open");
        Assert.Contains(targets, t => t.Path == @"Software\Classes\Directory\shell\open");
        Assert.Contains(targets, t => t.Path == @"Software\Classes\Drive\shell\open");
    }

    [Fact]
    public void Map_clsid_finds_shellex_handler_locations_via_default_or_keyname_match()
    {
        var reg = new FakeRegistry();
        // Handler registered with CLSID in default value:
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\WinRAR", "", "{ABC}");
        // Handler registered with CLSID as key name:
        reg.CreateKey(RegistryHive.ClassesRoot, @"Directory\shellex\ContextMenuHandlers\{ABC}");
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapClsid("{ABC}").ToList();

        Assert.Equal(2, targets.Count);
        Assert.All(targets, t => Assert.Equal(HideKind.HkcuMask, t.Kind));
        Assert.All(targets, t => Assert.Equal(RegistryHive.CurrentUser, t.Hive));
        Assert.Contains(targets, t => t.Path == @"Software\Classes\*\shellex\ContextMenuHandlers\WinRAR");
        Assert.Contains(targets, t => t.Path == @"Software\Classes\Directory\shellex\ContextMenuHandlers\{ABC}");
    }
}
