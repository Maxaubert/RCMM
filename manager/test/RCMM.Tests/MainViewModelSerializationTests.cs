using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;

namespace RCMM.Tests;

/// <summary>
/// Rescan and ApplyPending both run on background threads and mutate shared,
/// non-thread-safe state (_allRows, _backgroundExtsByClsid, the packaged maps). The
/// startup path fires RescanAsync and can trigger ApplyPending from the template-update
/// dialog before that scan finishes. They must be serialized. See the startup-race finding.
/// </summary>
public class MainViewModelSerializationTests
{
    [Fact]
    public void ApplyPending_waits_for_an_in_flight_Rescan_instead_of_running_concurrently()
    {
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var vm = Build(new BlockingCapture(entered, release));

        // Rescan enters CaptureAll and parks there, holding the work gate.
        var rescan = vm.RescanAsync();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "rescan never reached CaptureAll");

        // ApplyPending on another thread must block on the gate, not run concurrently.
        var apply = Task.Run(() => vm.ApplyPending());
        Assert.False(apply.Wait(TimeSpan.FromMilliseconds(400)),
            "ApplyPending ran while a Rescan was in flight — not serialized");

        release.Set();
        Assert.True(Task.WaitAll(new[] { rescan, apply }, TimeSpan.FromSeconds(10)),
            "operations did not both complete after the rescan was released");
    }

    private sealed class BlockingCapture : IContextMenuCaptureService
    {
        private readonly ManualResetEventSlim _entered, _release;
        public BlockingCapture(ManualResetEventSlim entered, ManualResetEventSlim release)
        { _entered = entered; _release = release; }

        public IReadOnlyList<CapturedItem> CaptureAll(IReadOnlyList<string> targetPaths)
        {
            _entered.Set();
            _release.Wait(TimeSpan.FromSeconds(10));
            return Array.Empty<CapturedItem>();
        }
    }

    private static MainViewModel Build(IContextMenuCaptureService capture)
    {
        var reg = new FakeRegistry();
        var files = new FakeFileVersionReader();
        var resolver = new ClsidResolver(reg);
        var shellexIndex = new ShellexNameIndex(reg, resolver, files);
        return new MainViewModel(
            capture, new TargetProvider(), new VerbToRegistryMapper(reg),
            new HideService(reg), reg, files, shellexIndex);
    }
}
