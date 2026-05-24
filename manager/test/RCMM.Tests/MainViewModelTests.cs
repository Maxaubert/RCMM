using System.Collections.Generic;
using System.IO;
using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;
using Xunit;

namespace RCMM.Tests;

public class MainViewModelTests : System.IDisposable
{
    private readonly string _tempRoot;
    private readonly TargetProvider _targets;

    public MainViewModelTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"rcmm-mvm-{System.Guid.NewGuid():N}");
        _targets = new TargetProvider(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private (MainViewModel vm, FakeRegistry reg, FakeContextMenuCaptureService cap) BuildSut()
    {
        var reg = new FakeRegistry();
        var cap = new FakeContextMenuCaptureService();
        var mapper = new VerbToRegistryMapper(reg);
        var hide = new HideService(reg);
        var files = new FakeFileVersionReader();
        var resolver = new ClsidResolver(reg);
        var shellexIndex = new ShellexNameIndex(reg, resolver, files);
        var vm = new MainViewModel(cap, _targets, mapper, hide, reg, files, shellexIndex);
        return (vm, reg, cap);
    }

    private string FirstFileTarget() => _targets.GetTargets().First(p => p.EndsWith(".txt"));

    [Fact]
    public void Rescan_populates_AllEntries_from_captured_items()
    {
        var (vm, reg, cap) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\git_shell", "", "Open Git Bash here");

        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "Open Git Bash here", Verb = "git_shell" }
        };

        vm.Rescan();

        Assert.Single(vm.AllEntries);
        Assert.Equal("Open Git Bash here", vm.AllEntries[0].DisplayName);
        Assert.True(vm.AllEntries[0].CanHide);
    }

    [Fact]
    public void Rescan_dedupes_same_verb_across_multiple_targets()
    {
        // Uses "edit" rather than "open": the generic "open" verb is intentionally
        // suppressed from the list (hiding it would break opening files/folders).
        var (vm, reg, cap) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\edit", "", "Edit");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\edit", "", "Edit");

        foreach (var target in _targets.GetTargets())
        {
            cap.Map[target] = new List<CapturedItem>
            {
                new() { TargetPath = target, Position = 0, DisplayName = "Edit", Verb = "edit" }
            };
        }

        vm.Rescan();

        Assert.Single(vm.AllEntries);
    }

    [Fact]
    public void Rescan_suppresses_the_generic_open_verb()
    {
        // "open" is the default action for files/folders/drives — hiding it would
        // make them un-openable, so it's never offered in the list.
        var (vm, reg, cap) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\open", "", "Open");
        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "Open", Verb = "open" }
        };

        vm.Rescan();

        Assert.DoesNotContain(vm.AllEntries, r => r.Entry.Id == "verb:open");
    }

    [Fact]
    public void Captured_item_with_no_registry_match_is_marked_as_unhideable()
    {
        var (vm, _, cap) = BuildSut();
        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "Cut", Verb = "cut" }
        };

        vm.Rescan();

        Assert.Single(vm.AllEntries);
        Assert.False(vm.AllEntries[0].CanHide);
    }

    [Fact]
    public void Toggle_records_pending_hide_with_full_target_list()
    {
        var (vm, reg, cap) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "", "Foo");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\foo", "", "Foo");

        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "Foo", Verb = "foo" }
        };

        vm.Rescan();
        vm.AllEntries[0].IsHidden = true;

        Assert.Single(vm.PendingChangeIds);
    }

    [Fact]
    public void ApplyPending_writes_LegacyDisable_to_every_HideTarget()
    {
        var (vm, reg, cap) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "", "Foo");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\foo", "", "Foo");

        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "Foo", Verb = "foo" }
        };

        vm.Rescan();
        vm.AllEntries[0].IsHidden = true;
        vm.ApplyPending();

        // Apply now writes to the per-user HKCU\Software\Classes shadow so it
        // doesn't need admin. The merged HKCR view picks LegacyDisable up from there.
        Assert.Equal("", reg.GetValue(RegistryHive.CurrentUser, @"Software\Classes\*\shell\foo", "LegacyDisable"));
        Assert.Equal("", reg.GetValue(RegistryHive.CurrentUser, @"Software\Classes\Directory\shell\foo", "LegacyDisable"));
    }

    [Fact]
    public void Shellex_toggle_sets_RequiresExplorerRestart()
    {
        var (vm, reg, cap) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\WinRAR", "", "{ABC}");

        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "WinRAR", OwnerClsid = "{ABC}" }
        };

        vm.Rescan();
        vm.AllEntries[0].IsHidden = true;

        Assert.True(vm.RequiresExplorerRestart);
    }

    [Fact]
    public void Shellex_item_without_Verb_is_hideable_when_DisplayName_matches_a_registered_handler()
    {
        var (vm, reg, cap) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\WinRAR", "", "{ABC}");
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{ABC}", "", "WinRAR Shell");

        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "WinRAR Shell", Verb = null, OwnerClsid = null }
        };

        vm.Rescan();

        Assert.Single(vm.AllEntries);
        Assert.True(vm.AllEntries[0].CanHide);
    }
}
