using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;
using Xunit;

namespace RCMM.Tests;

public class MainViewModelTests
{
    private static (MainViewModel vm, FakeRegistry reg) BuildSut()
    {
        var reg = new FakeRegistry();
        var scanner = new EntryScanner(
            new ClassicVerbScanner(reg),
            new ClassicShellexScanner(reg, new ClsidResolver(reg)));
        var hide = new HideService(reg);
        return (new MainViewModel(scanner, hide), reg);
    }

    [Fact]
    public void Rescan_populates_per_scope_lists()
    {
        var (vm, reg) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\a", "", "A");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\b", "", "B");

        vm.Rescan();

        Assert.Single(vm.GetScope(Scope.Files).Entries);
        Assert.Single(vm.GetScope(Scope.Folders).Entries);
        Assert.Empty(vm.GetScope(Scope.Drives).Entries);
    }

    [Fact]
    public void ToggleEntry_records_pending_change_and_does_not_yet_touch_registry()
    {
        var (vm, reg) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\a", "", "A");
        vm.Rescan();

        var row = vm.GetScope(Scope.Files).Entries.First();
        row.IsHidden = true;

        Assert.Single(vm.PendingChanges);
        Assert.Equal(PendingAction.Hide, vm.PendingChanges.First().Action);
        // Verb-only changes do NOT require an explorer restart
        Assert.False(vm.RequiresExplorerRestart);
    }

    [Fact]
    public void ApplyPending_writes_to_registry_and_clears_pending()
    {
        var (vm, reg) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\a", "", "A");
        vm.Rescan();

        vm.GetScope(Scope.Files).Entries.First().IsHidden = true;
        vm.ApplyPending();

        Assert.Equal("", reg.GetValue(RegistryHive.ClassesRoot, @"*\shell\a", "LegacyDisable"));
        Assert.Empty(vm.PendingChanges);
    }

    [Fact]
    public void Shellex_toggle_sets_RequiresExplorerRestart()
    {
        var (vm, reg) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\X", "", "{Y}");
        vm.Rescan();

        vm.GetScope(Scope.Files).Entries.First().IsHidden = true;

        Assert.True(vm.RequiresExplorerRestart);
    }
}
