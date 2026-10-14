using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;

namespace RCMM.Tests;

/// <summary>
/// ApplyPending runs on a Task.Run worker, but hiding an RCMM-owned entry goes
/// through AddPageViewModel.SetEntryHidden — an indexed Replace on the UI-bound
/// Entries ObservableCollection whose CollectionChanged handlers rebuild
/// XAML-bound rows (AddPage.RefreshAll). Per the threading design every such
/// mutation must be marshaled through the injected _post dispatcher; doing it
/// on the worker throws RPC_E_WRONG_THREAD in the real app and aborts the apply
/// mid-batch. This drives ApplyPending from a background thread while the test
/// thread plays the UI dispatcher, and asserts the collection mutation lands on
/// the dispatcher thread.
/// </summary>
public class MainViewModelOwnedHideThreadingTests : IDisposable
{
    private const string OwnedVerb = "RCMM.001.abc";
    private const string OwnedKeyHkcu = "Software\\Classes\\Directory\\Background\\shell\\" + OwnedVerb;

    private readonly string _storePath =
        Path.Combine(Path.GetTempPath(), $"rcmm-ownth-{Guid.NewGuid():N}", "additions.json");

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_storePath)!;
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Owned_hide_from_background_apply_mutates_AddPage_entries_on_the_dispatcher_thread()
    {
        var uiActions = new BlockingCollection<Action>();
        int uiThreadId = Environment.CurrentManagedThreadId;
        var (vm, addPage, reg) = BuildSut(postToUi: uiActions.Add);

        vm.Rescan();
        PumpAll(uiActions);          // rescan UI tail runs on the test ("UI") thread

        var mutationThreads = new List<int>();
        addPage.Entries.CollectionChanged += (_, __) =>
            mutationThreads.Add(Environment.CurrentManagedThreadId);

        var row = vm.AllEntries.Single(r => r.Entry.DisplayName == "My Entry");
        row.IsHidden = true;

        // Background apply, exactly like FooterApply_Click's Task.Run. The test
        // thread pumps the fake dispatcher so the worker's marshaled Add-page
        // operations (SetEntryHidden, Snapshot, MarkClean) can complete.
        var apply = Task.Run(() => vm.ApplyPending());
        while (!apply.IsCompleted)
        {
            if (uiActions.TryTake(out var action, 25)) action();
        }
        await apply;
        PumpAll(uiActions);          // apply UI tail + MarkClean

        // The hide really routed through SetEntryHidden...
        Assert.NotEmpty(mutationThreads);
        // ...and every Entries mutation ran on the dispatcher thread, never on
        // the apply worker.
        Assert.All(mutationThreads, t => Assert.Equal(uiThreadId, t));

        // End-to-end result unchanged: intent persisted, marker re-emitted.
        Assert.True(addPage.Entries.Single(e => e.Id == "abc").Hidden);
        Assert.False(addPage.HasPendingChanges);
        Assert.Equal("", reg.GetValue(RegistryHive.CurrentUser, OwnedKeyHkcu, "LegacyDisable"));
    }

    private static void PumpAll(BlockingCollection<Action> uiActions)
    {
        while (uiActions.TryTake(out var action)) action();
    }

    private (MainViewModel, AddPageViewModel, FakeRegistry) BuildSut(Action<Action> postToUi)
    {
        var reg = new FakeRegistry();
        var files = new FakeFileVersionReader();
        var mui = new FakeMuiStringResolver();
        var resolver = new ClsidResolver(reg);
        var shellexIndex = new ShellexNameIndex(reg, resolver, files);
        var verbScanner = new ClassicVerbScanner(reg, mui);
        var shellexScanner = new ClassicShellexScanner(reg, resolver, files);
        var entryScanner = new EntryScanner(verbScanner, shellexScanner);

        // The owned verb as the applier would have written it, visible in HKCR.
        reg.SetValue(RegistryHive.ClassesRoot, "Directory\\Background\\shell\\" + OwnedVerb, "", "My Entry");
        reg.SetValue(RegistryHive.ClassesRoot, "Directory\\Background\\shell\\" + OwnedVerb + "\\command", "", "cmd /k npm run dev");

        var addPage = new AddPageViewModel(new AdditionStore(_storePath));
        addPage.AddEntry(new AdditionEntry
        {
            Id = "abc", Name = "My Entry", Command = "npm run dev", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
            Hidden = false,
        });
        addPage.MarkClean();

        var vm = new MainViewModel(
            new FakeContextMenuCaptureService(), new TargetProvider(), new VerbToRegistryMapper(reg),
            new HideService(reg), reg, files, shellexIndex,
            registryScanner: entryScanner,
            addPage: addPage,
            additionApplier: new AdditionApplier(reg),
            knownStore: new KnownEntriesStore(
                Path.Combine(Path.GetDirectoryName(_storePath)!, "known-entries.json")),
            postToUi: postToUi);

        return (vm, addPage, reg);
    }
}
