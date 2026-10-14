using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;

namespace RCMM.Tests;

/// <summary>
/// The pending-change state lives in two places: the _pendingHide/_pendingUnhide
/// dictionaries (consumed by Apply) and the PendingChangeIds badge list (drives
/// the footer button). RescanCore used to clear the dictionaries on the worker
/// and the badge list later in the posted UI tail — a toggle landing in that gap
/// kept its dictionary entry but lost its badge, becoming an invisible change
/// silently applied later. ApplyPendingCore had the mirror bug: its tail wiped
/// the whole badge list, erasing badges for toggles made during the apply.
/// These tests hold the posted UI tail in a deferred list to step through the
/// exact race window deterministically.
/// </summary>
public class MainViewModelPendingClearRaceTests : IDisposable
{
    private readonly string _tempDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rcmm-pcr-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static void Drain(List<Action> deferred)
    {
        foreach (var action in deferred.ToList()) action();
        deferred.Clear();
    }

    [Fact]
    public void Toggle_between_rescan_worker_finish_and_UI_tail_is_not_silently_applied_later()
    {
        var deferred = new List<Action>();
        var vm = BuildWithRows(out var reg, postToUi: deferred.Add);

        vm.Rescan();
        Drain(deferred);
        var row = vm.AllEntries.First(r => r.Entry.DisplayName == "Verb 0");

        // Second rescan: the worker part runs to completion here while its UI
        // tail sits in `deferred` — exactly the window between the background
        // thread finishing and the dispatcher running the posted block.
        vm.Rescan();
        row.IsHidden = true;                     // UI-thread toggle inside the window
        Assert.Single(vm.PendingChangeIds);
        Drain(deferred);                          // rescan UI tail

        // The tail must wipe badge AND queue together — nothing half-pending.
        Assert.Empty(vm.PendingChangeIds);
        vm.ApplyPending();
        Drain(deferred);
        Assert.Null(reg.GetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\verb0", "LegacyDisable"));
    }

    [Fact]
    public void Toggle_during_apply_keeps_both_its_badge_and_its_queued_change()
    {
        var deferred = new List<Action>();
        var vm = BuildWithRows(out var reg, postToUi: deferred.Add);

        vm.Rescan();
        Drain(deferred);
        var rowA = vm.AllEntries.First(r => r.Entry.DisplayName == "Verb 0");
        var rowB = vm.AllEntries.First(r => r.Entry.DisplayName == "Verb 1");

        rowA.IsHidden = true;
        vm.ApplyPending();                        // worker part done; UI tail held
        rowB.IsHidden = true;                     // toggle lands during the apply window
        Drain(deferred);                          // apply UI tail

        // A's badge is gone (its change was applied); B's badge must survive.
        Assert.DoesNotContain(rowA.Entry.Id, vm.PendingChangeIds);
        Assert.Contains(rowB.Entry.Id, vm.PendingChangeIds);
        Assert.Equal("", reg.GetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\verb0", "LegacyDisable"));

        // And B's queued change still applies on the next Apply.
        vm.ApplyPending();
        Drain(deferred);
        Assert.Empty(vm.PendingChangeIds);
        Assert.Equal("", reg.GetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\verb1", "LegacyDisable"));
    }

    private MainViewModel BuildWithRows(out FakeRegistry reg, Action<Action> postToUi)
    {
        reg = new FakeRegistry();
        var files = new FakeFileVersionReader();
        var mui = new FakeMuiStringResolver();
        var resolver = new ClsidResolver(reg);
        var shellexIndex = new ShellexNameIndex(reg, resolver, files);
        var verbScanner = new ClassicVerbScanner(reg, mui);
        var shellexScanner = new ClassicShellexScanner(reg, resolver, files);
        var entryScanner = new EntryScanner(verbScanner, shellexScanner);

        for (int i = 0; i < 3; i++)
            reg.SetValue(RegistryHive.ClassesRoot, $"Directory\\Background\\shell\\verb{i}", "", $"Verb {i}");

        return new MainViewModel(
            new FakeContextMenuCaptureService(), new TargetProvider(), new VerbToRegistryMapper(reg),
            new HideService(reg), reg, files, shellexIndex,
            registryScanner: entryScanner,
            knownStore: new KnownEntriesStore(
                System.IO.Path.Combine(_tempDir, "known-entries.json")),
            postToUi: postToUi);
    }
}
