using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

/// <summary>
/// ShellexInvoker's 45s probe timeout abandons a still-running STA worker. The
/// published dictionaries must be immutable snapshots (the rename pass
/// enumerates them right after the timeout), late results from the abandoned
/// worker must be discarded, and the partial state must not be cached for the
/// whole process lifetime — once the abandoned worker actually exits, the next
/// build call retries from scratch. Uses the internal ProbeOverride seam so no
/// real COM handlers are involved.
/// </summary>
public class ShellexInvokerTimeoutTests : IDisposable
{
    private const string FastClsid = "{11111111-1111-1111-1111-111111111111}";
    private const string SlowClsid = "{22222222-2222-2222-2222-222222222222}";

    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"rcmm-invk-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private ShellexInvoker BuildSut()
    {
        var reg = new FakeRegistry();
        // Scope enumeration order is Files (*) first, then Directory — so the
        // fast handler is always probed (and published) before the slow one.
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\Fast", "", FastClsid);
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shellex\ContextMenuHandlers\Slow", "", SlowClsid);
        return new ShellexInvoker(reg, new TargetProvider(_tempRoot));
    }

    [Fact]
    public void Timeout_serves_partial_snapshot_discards_late_results_and_rebuilds_after_worker_exit()
    {
        var sut = BuildSut();
        sut.BuildTimeout = TimeSpan.FromSeconds(2);
        using var release = new ManualResetEventSlim(false);
        int probes = 0;
        sut.ProbeOverride = (clsid, _, emitted, icons, titles) =>
        {
            Interlocked.Increment(ref probes);
            if (string.Equals(clsid, SlowClsid, StringComparison.OrdinalIgnoreCase))
                release.Wait(TimeSpan.FromSeconds(20));
            var key = clsid.Trim().ToUpperInvariant();
            var name = string.Equals(clsid, FastClsid, StringComparison.OrdinalIgnoreCase) ? "Fast item" : "Slow item";
            emitted[key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
            titles[key] = name + " title";
        };

        // First build times out with only the fast handler published.
        var partial = sut.BuildDisplayNameToClsidMap();
        Assert.True(partial.ContainsKey("Fast item"));
        Assert.False(partial.ContainsKey("Slow item"));
        Assert.Equal("Fast item title", sut.LookupTitle(FastClsid));
        Assert.Contains("Fast item", sut.LookupEmittedNames(FastClsid));

        // While the abandoned worker is still blocked, later calls keep serving
        // the same partial snapshot without stacking a second build/wait.
        var again = sut.BuildDisplayNameToClsidMap();
        Assert.False(again.ContainsKey("Slow item"));
        int probesAfterTimeout = Volatile.Read(ref probes);

        // Unblock the abandoned worker. Its late slow-handler result must be
        // discarded; the complete map only appears via a fresh rebuild, proven
        // by the probe counter growing by both registrations again.
        release.Set();
        IReadOnlyDictionary<string, string>? full = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            full = sut.BuildDisplayNameToClsidMap();
            if (full.ContainsKey("Slow item")) break;
            Thread.Sleep(25);
        }
        Assert.NotNull(full);
        Assert.True(full!.ContainsKey("Slow item"));
        Assert.True(full.ContainsKey("Fast item"));
        Assert.Equal(probesAfterTimeout + 2, Volatile.Read(ref probes));

        // The rebuild completed, so it is cached again: no further probing.
        sut.BuildDisplayNameToClsidMap();
        Assert.Equal(probesAfterTimeout + 2, Volatile.Read(ref probes));
    }

    [Fact]
    public void Completed_build_is_cached_and_never_reprobed()
    {
        var sut = BuildSut();
        int probes = 0;
        sut.ProbeOverride = (clsid, _, emitted, icons, titles) =>
        {
            Interlocked.Increment(ref probes);
            emitted[clsid.Trim().ToUpperInvariant()] =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Item " + clsid };
        };

        var map1 = sut.BuildDisplayNameToClsidMap();
        var map2 = sut.BuildDisplayNameToClsidMap();

        Assert.Equal(2, probes);
        Assert.True(map2.ContainsKey("Item " + FastClsid));
        Assert.True(map2.ContainsKey("Item " + SlowClsid));
        Assert.Equal(map1.Count, map2.Count);
    }
}
