using System;
using System.IO;
using System.Linq;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;
using Xunit;

namespace RCMM.Tests;

/// <summary>
/// Drives HideService / the ViewModel against the REAL Win32Registry — but with
/// every HKCU access remapped into a throwaway sandbox subtree
/// (Software\RCMMTests\&lt;tag&gt;) so nothing can touch the user's live registry:
/// the old save/restore-in-finally approach left the real WinRAR CLSID in the
/// real Blocked list whenever the test host died mid-test, silently removing
/// WinRAR's context-menu entries at the next Explorer restart. HKCR/HKLM reads
/// pass through so discovery still sees the real machine.
/// </summary>
[Trait("Integration", "true")]
public sealed class ApplyEndToEndTests : IDisposable
{
    private const string TestClsid = "{B41DB860-64E4-11D2-9906-E49FADC173CA}"; // WinRAR

    private readonly string _testTag = "E2E_" + Guid.NewGuid().ToString("N").Substring(0, 8);
    private readonly Win32Registry _real = new();
    private readonly IRegistry _reg;
    private readonly string _tempDir;

    public ApplyEndToEndTests()
    {
        _reg = new SandboxedRegistry(_real, "Software\\RCMMTests\\" + _testTag);
        _tempDir = Path.Combine(Path.GetTempPath(), $"rcmm-e2e-{Guid.NewGuid():N}");
    }

    /// <summary>Deletes only this instance's sandbox subtree — other integration
    /// test classes share the Software\RCMMTests root and may be running in
    /// parallel, so sweeping the whole root here would race with them.</summary>
    public void Dispose()
    {
        try { _real.DeleteKey(RegistryHive.CurrentUser, "Software\\RCMMTests\\" + _testTag); }
        catch { /* best-effort cleanup */ }
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Toggling_and_applying_a_packaged_item_writes_to_blocked_list()
    {
        var hide = new HideService(_reg);
        var target = HideService.BlockedShellExtTarget(TestClsid);

        // Direct exercise: HideService.Hide should write the value.
        hide.Hide(new[] { target });
        var afterHide = _reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, TestClsid);
        Assert.Equal("", afterHide);

        // And HideService.Unhide should remove it.
        hide.Unhide(new[] { target });
        var afterUnhide = _reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, TestClsid);
        Assert.Null(afterUnhide);
    }

    [Fact]
    public void LegacyDisable_HideTarget_uses_HKCU_so_apply_works_without_admin()
    {
        // We construct the HideTarget exactly as VerbToRegistryMapper would.
        var target = new RCMM.Core.Models.HideTarget(
            RCMM.Core.Models.HideKind.LegacyDisable,
            RegistryHive.CurrentUser,
            @"Software\Classes\*\shell\RCMMTestProbeVerb",
            "LegacyDisable");
        var hide = new HideService(_reg);

        hide.Hide(new[] { target });
        Assert.Equal("", _reg.GetValue(RegistryHive.CurrentUser, target.Path, "LegacyDisable"));

        hide.Unhide(new[] { target });
        Assert.Null(_reg.GetValue(RegistryHive.CurrentUser, target.Path, "LegacyDisable"));
    }

    [SkippableFact]
    public void ViewModel_OnRowToggled_then_ApplyPending_writes_to_registry()
    {
        // Build minimal services. Capture is stubbed (returns nothing) so we control
        // what rows the ViewModel produces; the packaged scanner is real.
        var files = new Win32FileVersionReader();
        var mui = new Win32MuiStringResolver();
        var resolver = new ClsidResolver(_reg);
        var shellexIndex = new ShellexNameIndex(_reg, resolver, files);
        var verbScanner = new ClassicVerbScanner(_reg, mui);
        var shellexScanner = new ClassicShellexScanner(_reg, resolver, files);
        var entryScanner = new EntryScanner(verbScanner, shellexScanner);
        var packagedScanner = new PackagedShellExtScanner(_reg, mui);
        var mapper = new VerbToRegistryMapper(_reg);
        var capture = new FakeContextMenuCaptureService();
        var hide = new HideService(_reg);
        var targets = new TargetProvider();

        var vm = new MainViewModel(capture, targets, mapper, hide, _reg, files, shellexIndex,
            entryScanner, packagedScanner,
            knownStore: new KnownEntriesStore(Path.Combine(_tempDir, "known-entries.json")));

        vm.Rescan();

        // Find the WinRAR row. Whether the scanner surfaces it depends on this
        // machine (WinRAR installed or not) — skip visibly instead of silently
        // passing green with zero assertions executed.
        var winrar = vm.AllEntries.FirstOrDefault(r =>
            string.Equals(r.Entry.Id, "clsid:" + TestClsid.ToLowerInvariant()));
        Skip.If(winrar == null,
            "WinRAR's shell extension was not surfaced on this machine — the toggle/apply flow can't be exercised.");

        Assert.True(winrar!.CanHide);
        Assert.False(winrar.IsHidden);
        Assert.Contains(winrar.Entry.HideTargets, t =>
            t.Path == HideService.BlockedListPath && t.ValueName == TestClsid);

        // Simulate the UI toggle.
        winrar.IsHidden = true;
        Assert.Contains(winrar.Entry.Id, vm.PendingChangeIds);

        vm.ApplyPending();

        var afterApply = _reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, TestClsid);
        Assert.Equal("", afterApply);
    }
}
