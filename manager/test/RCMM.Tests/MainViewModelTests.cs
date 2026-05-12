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
            new ClassicVerbScanner(reg, new FakeMuiStringResolver()),
            new ClassicShellexScanner(reg, new ClsidResolver(reg), new FakeFileVersionReader()));
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
        // Give the handler a resolvable CLSID DefaultName so it is not dropped.
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{Y}", "", "X Extension");
        vm.Rescan();

        vm.GetScope(Scope.Files).Entries.First().IsHidden = true;

        Assert.True(vm.RequiresExplorerRestart);
    }

    [Fact]
    public void AllEntries_combines_user_visible_entries_across_scopes()
    {
        var (vm, reg) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\Open Git Bash here", "", "Open Git Bash here");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\Edit with Notepad", "", "Edit with Notepad");
        vm.Rescan();

        Assert.Equal(2, vm.AllEntries.Count);
        Assert.Contains(vm.AllEntries, r => r.DisplayName == "Open Git Bash here");
        Assert.Contains(vm.AllEntries, r => r.DisplayName == "Edit with Notepad");
    }

    [Fact]
    public void AllEntries_filters_out_clsid_and_path_display_names()
    {
        var (vm, reg) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\OpenWith", "", "Open with");
        // Handler whose CLSID DefaultName is a DLL path — PickDisplay will return the path string,
        // but EntryFilters.IsLikelyUserVisible will reject it (contains backslash).
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\Junk", "", "{B41DB860-8EE4-11D2-9906-E49FADC173CA}");
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{B41DB860-8EE4-11D2-9906-E49FADC173CA}", "", @"C:\Windows\System32\shell32.dll");
        vm.Rescan();

        // The user-visible "Open with" verb makes it through; the path/CLSID handler does not.
        Assert.Single(vm.AllEntries);
        Assert.Equal("Open with", vm.AllEntries[0].DisplayName);
    }

    [Fact]
    public void AllEntries_dedupes_same_kind_and_key_across_scopes()
    {
        var (vm, reg) = BuildSut();
        // Same handler registered under both Files and Folders scopes.
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\WinRAR", "", "{ABC}");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shellex\ContextMenuHandlers\WinRAR", "", "{ABC}");
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{ABC}", "", "WinRAR Shell");
        vm.Rescan();

        Assert.Single(vm.AllEntries);
        Assert.Equal("WinRAR Shell", vm.AllEntries[0].DisplayName);
    }

    [Fact]
    public void AllEntries_includes_builtin_entries_by_default()
    {
        var (vm, reg) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\thirdparty", "", "Third party");
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\thirdparty\command", "", @"C:\Program Files\Vendor\app.exe");
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\sysverb", "", "Sys Verb");
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\sysverb\command", "", @"%SystemRoot%\System32\sysfoo.exe");
        vm.Rescan();

        Assert.Equal(2, vm.AllEntries.Count);
        Assert.Contains(vm.AllEntries, r => r.DisplayName == "Third party");
        Assert.Contains(vm.AllEntries, r => r.DisplayName == "Sys Verb");
    }

    [Fact]
    public void Clearing_ShowBuiltIns_removes_builtin_entries_from_AllEntries()
    {
        var (vm, reg) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\thirdparty", "", "Third party");
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\thirdparty\command", "", @"C:\Program Files\Vendor\app.exe");
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\sysverb", "", "Sys Verb");
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\sysverb\command", "", @"%SystemRoot%\System32\sysfoo.exe");
        vm.Rescan();

        Assert.Equal(2, vm.AllEntries.Count);
        vm.ShowBuiltIns = false;
        Assert.Single(vm.AllEntries);
        Assert.Equal("Third party", vm.AllEntries[0].DisplayName);
    }
}
