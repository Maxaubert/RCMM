using System;
using System.Collections.Generic;
using System.Threading;
using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

/// <summary>
/// CaptureAll's STA worker can outlive the 30s timeout (that's what the timeout
/// is for). The caller must get a snapshot the abandoned worker can no longer
/// mutate — RescanCore AddRange-enumerates the returned list immediately, and a
/// concurrent Add from the worker corrupts or throws mid-rescan. Exercises the
/// internal seam (injected per-target capture + timeout) so no real COM runs.
/// </summary>
public class ContextMenuCaptureTimeoutTests
{
    private static CapturedItem Item(string path) => new()
    {
        TargetPath = path,
        Position = 0,
        DisplayName = path,
    };

    [Fact]
    public void Timeout_returns_partial_snapshot_that_late_worker_results_cannot_mutate()
    {
        using var release = new ManualResetEventSlim(false);
        using var workerExited = new ManualResetEventSlim(false);
        void CaptureOne(string path, List<CapturedItem> sink)
        {
            if (path == "slow") release.Wait(TimeSpan.FromSeconds(20));
            sink.Add(Item(path));
        }

        var result = ContextMenuCaptureService.CaptureAll(
            new[] { "fast", "slow" }, CaptureOne, TimeSpan.FromSeconds(2), workerExited);

        Assert.Single(result);
        Assert.Equal("fast", result[0].TargetPath);

        // Let the abandoned worker finish its slow target. Its late item must be
        // discarded — the list we were handed cannot grow behind our back.
        release.Set();
        Assert.True(workerExited.Wait(TimeSpan.FromSeconds(10)), "abandoned worker never exited");
        Assert.Single(result);
    }

    [Fact]
    public void Completed_capture_returns_every_target_item()
    {
        var result = ContextMenuCaptureService.CaptureAll(
            new[] { "a", "b" },
            (path, sink) => sink.Add(Item(path)),
            TimeSpan.FromSeconds(30));

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Faulting_target_keeps_its_partial_items_and_later_targets_still_captured()
    {
        var result = ContextMenuCaptureService.CaptureAll(
            new[] { "boom", "ok" },
            (path, sink) =>
            {
                sink.Add(Item(path));
                if (path == "boom") throw new InvalidOperationException("handler blew up");
            },
            TimeSpan.FromSeconds(30));

        // Same per-target tolerance as before the publication refactor: items
        // added before the throw survive, the next target is still probed.
        Assert.Equal(2, result.Count);
    }
}
