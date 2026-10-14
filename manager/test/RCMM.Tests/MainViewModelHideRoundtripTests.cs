using System.Collections.Generic;
using System.IO;
using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;
using Xunit;

namespace RCMM.Tests;

/// <summary>
/// Hide → Apply → Rescan roundtrips. A hidden entry must never vanish from the
/// list: for classic verbs the registry scan plus the merged-HKCR LegacyDisable
/// read keeps it; for shellex CLSIDs the step-9 filter's !IsHidden retention
/// clause keeps it; for CommandStore-only verbs (Windows.Share / "Give access
/// to") RestoreGhostEntries is the only recovery path — mechanism A restores
/// from the persisted known-entries store, mechanism B walks CommandStore for
/// blocked verbs hidden before the store existed. A regression in any of these
/// leaves the entry permanently un-unhidable from the UI. Review b09-tests #8.
/// </summary>
public class MainViewModelHideRoundtripTests : System.IDisposable
{
    private const string CommandStoreRoot =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell";

    private readonly string _tempRoot;
    private readonly TargetProvider _targets;

    public MainViewModelHideRoundtripTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"rcmm-mvmrt-{System.Guid.NewGuid():N}");
        _targets = new TargetProvider(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private (MainViewModel vm, FakeRegistry reg, FakeContextMenuCaptureService cap, FakeFileVersionReader files)
        BuildSut(bool withCommandStore = false)
    {
        var reg = new FakeRegistry();
        var cap = new FakeContextMenuCaptureService();
        var files = new FakeFileVersionReader();
        var resolver = new ClsidResolver(reg);
        var vm = new MainViewModel(
            cap, _targets, new VerbToRegistryMapper(reg), new HideService(reg), reg, files,
            new ShellexNameIndex(reg, resolver, files),
            registryScanner: new EntryScanner(
                new ClassicVerbScanner(reg, new FakeMuiStringResolver()),
                new ClassicShellexScanner(reg, resolver, files)),
            commandStore: withCommandStore ? new CommandStoreVerbIndex(reg) : null,
            knownStore: new KnownEntriesStore(Path.Combine(_tempRoot, "known-entries.json")));
        return (vm, reg, cap, files);
    }

    private string FirstFileTarget() => _targets.GetTargets().First(p => p.EndsWith(".txt"));

    // ---- Classic verb: hide survives the rescan, un-hide restores it ----

    [Fact]
    public void Hidden_classic_verb_stays_listed_as_hidden_after_rescan_and_can_be_unhidden()
    {
        var (vm, reg, cap, _) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\Background\shell\Tabby", "", "Open Tabby here");
        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "Open Tabby here", Verb = "Tabby" }
        };

        vm.Rescan();
        vm.AllEntries.Single(r => r.Entry.Id == "verb:tabby").IsHidden = true;
        Assert.Empty(vm.ApplyPending());

        // Explorer stops returning a LegacyDisable'd verb — only the registry
        // scan (through the merged HKCR view) can still discover it.
        cap.Map.Clear();
        vm.Rescan();

        var row = vm.AllEntries.Single(r => r.Entry.Id == "verb:tabby");
        Assert.True(row.Entry.IsHidden);
        Assert.True(row.CanHide);

        row.IsHidden = false;
        Assert.Empty(vm.ApplyPending());
        Assert.Null(reg.GetValue(RegistryHive.CurrentUser,
            @"Software\Classes\Directory\Background\shell\Tabby", "LegacyDisable"));

        vm.Rescan();
        Assert.False(vm.AllEntries.Single(r => r.Entry.Id == "verb:tabby").Entry.IsHidden);
    }

    // ---- Shellex CLSID row: the step-9 !IsHidden retention clause ----

    [Fact]
    public void Hidden_shellex_clsid_row_is_retained_by_the_filter_and_can_be_unhidden()
    {
        // Once hidden, the handler is no longer observed live and is neither
        // packaged, renamed nor CommandStore-known — the ONLY thing keeping the
        // registry-derived row visible is the filter's IsHidden retention clause.
        var (vm, reg, cap, files) = BuildSut();
        var clsid = "{11111111-2222-3333-4444-555555555555}";
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shellex\ContextMenuHandlers\Recuva", "", clsid);
        reg.SetValue(RegistryHive.ClassesRoot, $@"CLSID\{clsid}\InprocServer32", "", @"C:\Tools\recuva64.dll");
        files.Map[@"C:\Tools\recuva64.dll"] = new FileVersion("Scan for deleted files", "Piriform", null);

        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "Scan for deleted files", OwnerClsid = clsid }
        };

        vm.Rescan();
        vm.AllEntries.Single(r => r.Entry.Id == $"clsid:{clsid.ToLowerInvariant()}").IsHidden = true;
        Assert.Empty(vm.ApplyPending());
        // The HkcuMask shadow is in place.
        Assert.Equal("", reg.GetValue(RegistryHive.CurrentUser,
            @"Software\Classes\Directory\shellex\ContextMenuHandlers\Recuva", ""));

        // Explorer no longer surfaces the masked handler.
        cap.Map.Clear();
        vm.Rescan();

        var row = vm.AllEntries.Single(r => r.Entry.Id == $"clsid:{clsid.ToLowerInvariant()}");
        Assert.True(row.Entry.IsHidden);
        Assert.True(row.CanHide);

        row.IsHidden = false;
        Assert.Empty(vm.ApplyPending());
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser,
            @"Software\Classes\Directory\shellex\ContextMenuHandlers\Recuva"));
    }

    // ---- Ghost mechanism A: restore from the persisted known-entries store ----

    [Fact]
    public void Hidden_CommandStore_only_verb_is_restored_as_a_ghost_and_can_be_unhidden()
    {
        var (vm, reg, cap, _) = BuildSut(withCommandStore: true);
        var clsid = "{E2BF9676-5F8F-435C-97EB-11607A5BEDF7}";
        reg.SetValue(RegistryHive.LocalMachine, CommandStoreRoot + @"\Windows.Share",
            "ExplorerCommandHandler", clsid);

        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "Give access to", Verb = "Windows.Share" }
        };

        vm.Rescan();
        vm.AllEntries.Single(r => r.Entry.Id == "verb:windows.share").IsHidden = true;
        Assert.Empty(vm.ApplyPending());
        Assert.NotNull(reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, clsid));

        // The blocked handler stops contributing to live captures, and there is
        // no HKCR\<scope>\shell key for the scanners — without the ghost restore
        // the entry vanishes with no way to un-toggle it.
        cap.Map.Clear();
        vm.Rescan();

        var ghost = vm.AllEntries.Single(r => r.Entry.Id == "verb:windows.share");
        Assert.True(ghost.Entry.IsHidden);
        Assert.True(ghost.CanHide);
        Assert.Equal("Give access to", ghost.DisplayName);

        ghost.IsHidden = false;
        Assert.Empty(vm.ApplyPending());
        Assert.Null(reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, clsid));
    }

    [Fact]
    public void Unhidden_ghost_is_not_resurrected_by_the_next_rescan()
    {
        // The AllTargetsHidden gate: once the user un-hides (or the markers are
        // gone), a stored entry that no scan can see any more must NOT come back.
        var (vm, reg, cap, _) = BuildSut(withCommandStore: true);
        var clsid = "{E2BF9676-5F8F-435C-97EB-11607A5BEDF7}";
        reg.SetValue(RegistryHive.LocalMachine, CommandStoreRoot + @"\Windows.Share",
            "ExplorerCommandHandler", clsid);

        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "Give access to", Verb = "Windows.Share" }
        };

        vm.Rescan();
        vm.AllEntries.Single(r => r.Entry.Id == "verb:windows.share").IsHidden = true;
        Assert.Empty(vm.ApplyPending());

        cap.Map.Clear();
        vm.Rescan();
        vm.AllEntries.Single(r => r.Entry.Id == "verb:windows.share").IsHidden = false;
        Assert.Empty(vm.ApplyPending());

        // Simulates the verb also being genuinely gone from the menu (e.g. the
        // OS feature removed): nothing hidden, nothing captured → no row.
        vm.Rescan();
        Assert.DoesNotContain(vm.AllEntries, r => r.Entry.Id == "verb:windows.share");
    }

    // ---- Ghost mechanism B: CommandStore blocked-verb recovery, no store record ----

    [Fact]
    public void Blocked_CommandStore_verb_with_no_store_record_is_surfaced_as_hidden()
    {
        // The user hid Windows.Share before the known-entries store existed (or
        // the store file was lost): the Blocked list is set, no live capture, no
        // persisted entry. The CommandStore walk is the only recovery path.
        var (vm, reg, _, _) = BuildSut(withCommandStore: true);
        var clsid = "{E2BF9676-5F8F-435C-97EB-11607A5BEDF7}";
        reg.SetValue(RegistryHive.LocalMachine, CommandStoreRoot + @"\Windows.Share",
            "ExplorerCommandHandler", clsid);
        reg.SetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, clsid, "");

        vm.Rescan();

        var ghost = vm.AllEntries.Single(r => r.Entry.Id == "verb:windows.share");
        Assert.True(ghost.Entry.IsHidden);
        Assert.True(ghost.Entry.IsBuiltIn);
        Assert.Equal("Give access to", ghost.DisplayName);

        ghost.IsHidden = false;
        Assert.Empty(vm.ApplyPending());
        Assert.Null(reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, clsid));
    }

    [Fact]
    public void CommandStore_walk_skips_verbs_whose_clsids_are_already_covered_by_a_restored_ghost()
    {
        // Windows.Share and Windows.SharePrivate route through the same handler
        // CLSID. Mechanism A restores the Share ghost (adding its CLSID to the
        // covered set); the CommandStore walk must then skip SharePrivate rather
        // than adding a second row that toggles the same handler.
        var (vm, reg, cap, _) = BuildSut(withCommandStore: true);
        var clsid = "{E2BF9676-5F8F-435C-97EB-11607A5BEDF7}";
        reg.SetValue(RegistryHive.LocalMachine, CommandStoreRoot + @"\Windows.Share",
            "ExplorerCommandHandler", clsid);
        reg.SetValue(RegistryHive.LocalMachine, CommandStoreRoot + @"\Windows.SharePrivate",
            "ExplorerCommandHandler", clsid);

        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "Give access to", Verb = "Windows.Share" }
        };

        vm.Rescan();
        vm.AllEntries.Single(r => r.Entry.Id == "verb:windows.share").IsHidden = true;
        Assert.Empty(vm.ApplyPending());

        cap.Map.Clear();
        vm.Rescan();

        Assert.Contains(vm.AllEntries, r => r.Entry.Id == "verb:windows.share");
        Assert.DoesNotContain(vm.AllEntries, r => r.Entry.Id == "verb:windows.shareprivate");
        // Exactly one row manages the blocked handler.
        Assert.Single(vm.AllEntries, r => r.Entry.HideTargets.Any(
            t => t.Kind == HideKind.BlockedShellExt
                 && string.Equals(t.ValueName, clsid, System.StringComparison.OrdinalIgnoreCase)));
    }
}
