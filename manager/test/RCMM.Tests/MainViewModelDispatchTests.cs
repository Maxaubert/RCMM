using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;
using Xunit;

namespace RCMM.Tests;

public class MainViewModelDispatchTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly TargetProvider _targets;

    public MainViewModelDispatchTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"rcmm-disp-{Guid.NewGuid():N}");
        _targets = new TargetProvider(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // Mirrors MainViewModelTests.BuildSut + the single-row capture scenario,
    // but lets the caller supply postToUi.
    private MainViewModel BuildSut(Action<Action>? postToUi)
    {
        var reg = new FakeRegistry();
        var cap = new FakeContextMenuCaptureService();
        var mapper = new VerbToRegistryMapper(reg);
        var hide = new HideService(reg);
        var files = new FakeFileVersionReader();
        var resolver = new ClsidResolver(reg);
        var shellexIndex = new ShellexNameIndex(reg, resolver, files);

        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\git_shell", "", "Open Git Bash here");
        var target = _targets.GetTargets().First(p => p.EndsWith(".txt"));
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "Open Git Bash here", Verb = "git_shell" }
        };

        return new MainViewModel(cap, _targets, mapper, hide, reg, files, shellexIndex, postToUi: postToUi);
    }

    [Fact]
    public void Rescan_defers_collection_mutations_to_postToUi()
    {
        var deferred = new List<Action>();
        var vm = BuildSut(postToUi: deferred.Add);

        vm.Rescan();

        Assert.Empty(vm.AllEntries);
        Assert.NotEmpty(deferred);

        foreach (var action in deferred.ToList()) action();
        Assert.Single(vm.AllEntries);
        Assert.Equal("Open Git Bash here", vm.AllEntries[0].DisplayName);
    }

    [Fact]
    public void ApplyPending_defers_collection_mutations_to_postToUi()
    {
        var deferred = new List<Action>();
        var vm = BuildSut(postToUi: deferred.Add);

        vm.Rescan();
        foreach (var action in deferred.ToList()) action(); // flush rescan UI tail to populate AllEntries
        deferred.Clear();

        vm.AllEntries[0].IsHidden = true;        // toggle runs inline (UI-thread path), not via _post
        Assert.Single(vm.PendingChangeIds);

        vm.ApplyPending();

        // ApplyPending's UI tail (PendingChangeIds.Clear) is deferred, not run inline.
        Assert.Single(vm.PendingChangeIds);
        Assert.NotEmpty(deferred);

        foreach (var action in deferred.ToList()) action();
        Assert.Empty(vm.PendingChangeIds);
    }
}
