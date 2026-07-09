using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;

namespace RCMM.Tests;

/// <summary>
/// ApplyPending / RescanCore run on a background thread (Task.Run) while row
/// toggles fire OnRowToggled on the UI thread. Both touch the pending
/// dictionaries. Without a lock, a toggle landing during Apply throws
/// InvalidOperationException (collection modified during enumeration) or corrupts
/// the dictionary. This exercises that interleaving. See issue in the audit.
/// </summary>
public class MainViewModelToggleRaceTests
{
    [Fact]
    public void Concurrent_toggles_during_ApplyPending_and_Rescan_do_not_throw()
    {
        var vm = BuildWithRows(out var reg, rowCount: 12);
        vm.Rescan();
        var rows = vm.AllEntries.ToList();
        Assert.NotEmpty(rows);

        Exception? captured = null;
        var stop = false;

        // Background: what MainWindow does — Task.Run(ApplyPending) then RescanAsync,
        // in a tight loop to widen the window the UI-thread toggles can hit.
        var worker = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 200 && !Volatile.Read(ref stop); i++)
                {
                    vm.ApplyPending();
                    vm.Rescan();
                }
            }
            catch (Exception ex) { captured = ex; }
        });

        // "UI thread": flip rows while the worker applies/rescans.
        try
        {
            for (int i = 0; i < 4000 && captured == null; i++)
            {
                var row = rows[i % rows.Count];
                row.IsHidden = !row.IsHidden;
            }
        }
        catch (Exception ex) { captured = ex; }

        Volatile.Write(ref stop, true);
        worker.Wait(TimeSpan.FromSeconds(30));

        Assert.Null(captured);
    }

    private static MainViewModel BuildWithRows(out FakeRegistry reg, int rowCount)
    {
        reg = new FakeRegistry();
        var files = new FakeFileVersionReader();
        var mui = new FakeMuiStringResolver();
        var resolver = new ClsidResolver(reg);
        var shellexIndex = new ShellexNameIndex(reg, resolver, files);
        var verbScanner = new ClassicVerbScanner(reg, mui);
        var shellexScanner = new ClassicShellexScanner(reg, resolver, files);
        var entryScanner = new EntryScanner(verbScanner, shellexScanner);

        // A batch of plain classic verbs the scanner will surface as hideable rows.
        for (int i = 0; i < rowCount; i++)
            reg.SetValue(RegistryHive.ClassesRoot, $"Directory\\Background\\shell\\verb{i}", "", $"Verb {i}");

        return new MainViewModel(
            new FakeContextMenuCaptureService(), new TargetProvider(), new VerbToRegistryMapper(reg),
            new HideService(reg), reg, files, shellexIndex,
            registryScanner: entryScanner);
    }
}
