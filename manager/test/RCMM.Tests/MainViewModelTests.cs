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
        // Temp-backed store: Rescan persists known entries unconditionally, and the
        // default path is the developer's real %LOCALAPPDATA%\RCMM\known-entries.json.
        var vm = new MainViewModel(cap, _targets, mapper, hide, reg, files, shellexIndex,
            knownStore: new KnownEntriesStore(KnownStorePath()));
        return (vm, reg, cap);
    }

    private string KnownStorePath() => Path.Combine(_tempRoot, "known-entries.json");

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
    public void Rescan_persists_known_entries_to_the_injected_store_path()
    {
        // Regression guard for the test-isolation fix: MainViewModel used to
        // hard-wire KnownEntriesStore.DefaultPath(), so every test Rescan()
        // overwrote the developer's real known-entries.json with fake rows.
        var (vm, reg, cap) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\git_shell", "", "Open Git Bash here");
        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "Open Git Bash here", Verb = "git_shell" }
        };

        vm.Rescan();

        Assert.True(File.Exists(KnownStorePath()), "Rescan should save through the injected store");
        Assert.Contains("Open Git Bash here", File.ReadAllText(KnownStorePath()));
    }

    [Fact]
    public void Rescan_dedupes_same_verb_across_multiple_targets()
    {
        // Uses a vendor-style verb: the generic system verbs (open, edit, print, …)
        // are intentionally suppressed from the list — hiding them fans out far
        // beyond the single row and breaks core shell behavior.
        var (vm, reg, cap) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\vendortool", "", "Vendor Tool");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\vendortool", "", "Vendor Tool");

        foreach (var target in _targets.GetTargets())
        {
            cap.Map[target] = new List<CapturedItem>
            {
                new() { TargetPath = target, Position = 0, DisplayName = "Vendor Tool", Verb = "vendortool" }
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

    [Theory]
    [InlineData("print")]
    [InlineData("edit")]
    [InlineData("explore")]
    [InlineData("runas")]
    [InlineData("printto")]
    [InlineData("openas")]
    public void Rescan_suppresses_generic_system_verbs(string verb)
    {
        // Generic system verbs are shared by many unrelated progids and invoked
        // programmatically via ShellExecute — a LegacyDisable on them reaches far
        // past the single visible row, so they are never offered as toggles.
        var (vm, reg, cap) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, $@"*\shell\{verb}", "", verb);
        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = verb, Verb = verb }
        };

        vm.Rescan();

        Assert.DoesNotContain(vm.AllEntries, r => r.Entry.Id == "verb:" + verb);
    }

    [Fact]
    public void Hiding_a_verb_only_touches_the_file_types_it_was_captured_on()
    {
        // "vendorprint" is registered by two unrelated apps: under txtfile (the
        // .txt default progid) and under batfile. The row was captured on the
        // .txt sample only, so applying the hide must write LegacyDisable into
        // txtfile — and never into batfile (the old bare-verb-name association
        // index fanned out to every progid sharing the name).
        var (vm, reg, cap) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, ".txt", "", "txtfile");
        reg.SetValue(RegistryHive.ClassesRoot, @"txtfile\shell\vendorprint", "", "Vendor Print");
        reg.SetValue(RegistryHive.ClassesRoot, ".bat", "", "batfile");
        reg.SetValue(RegistryHive.ClassesRoot, @"batfile\shell\vendorprint", "", "Vendor Print");

        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "Vendor Print", Verb = "vendorprint" }
        };

        vm.Rescan();
        var row = vm.AllEntries.Single(r => r.Entry.Id == "verb:vendorprint");
        row.IsHidden = true;
        vm.ApplyPending();

        Assert.Equal("", reg.GetValue(RegistryHive.CurrentUser,
            @"Software\Classes\txtfile\shell\vendorprint", "LegacyDisable"));
        Assert.Null(reg.GetValue(RegistryHive.CurrentUser,
            @"Software\Classes\batfile\shell\vendorprint", "LegacyDisable"));
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser, @"Software\Classes\batfile\shell\vendorprint"));
    }

    [Fact]
    public void Active_per_user_shellex_is_not_mislabeled_as_hidden_and_can_be_toggled()
    {
        // A per-user-installed handler's REAL registration lives at the exact
        // HKCU path RCMM uses as its mask key, with the CLSID as default value.
        // Key existence alone must not count as "hidden": the handler is active,
        // and mislabeling it made both hide and unhide toggles dead.
        var (vm, reg, cap) = BuildSut();
        var clsid = "{11111111-2222-3333-4444-555555555555}";
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\PerUserHandler", "", clsid);
        reg.SetValue(RegistryHive.CurrentUser,
            @"Software\Classes\*\shellex\ContextMenuHandlers\PerUserHandler", "", clsid);

        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "Per User Handler", OwnerClsid = clsid }
        };

        vm.Rescan();

        var row = vm.AllEntries.Single();
        Assert.False(row.Entry.IsHidden);

        // Toggling hidden must register a pending change (previously it looked
        // like a no-change because AllTargetsHidden already reported hidden).
        row.IsHidden = true;
        Assert.Contains(row.Entry.Id, vm.PendingChangeIds);
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
        // Full-length CLSID: the name index only admits real CLSID-shaped
        // registrations (strict shared LooksLikeClsid, review b10).
        var clsid = "{AABBCCDD-1111-2222-3333-444455556666}";
        var (vm, reg, cap) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\WinRAR", "", clsid);
        reg.SetValue(RegistryHive.ClassesRoot, $@"CLSID\{clsid}", "", "WinRAR Shell");

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
