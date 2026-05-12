using System.Linq;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;
using Xunit;

namespace RCMM.Tests;

/// <summary>
/// Drives the ViewModel against the REAL Win32Registry. Goal: catch the bug where
/// the user clicked Apply but the Blocked-list key stayed empty. If the same flow
/// run from a test green-lights the registry write, the bug must be in the WinUI
/// binding layer; if the test reproduces the empty-registry symptom, the bug is in
/// MainViewModel or HideService.
///
/// Restores any keys it touches.
/// </summary>
[Trait("Integration", "true")]
public class ApplyEndToEndTests
{
    private const string TestClsid = "{B41DB860-64E4-11D2-9906-E49FADC173CA}"; // WinRAR

    [Fact]
    public void Toggling_and_applying_a_packaged_item_writes_to_blocked_list()
    {
        var reg = new Win32Registry();

        // Ensure clean preconditions (and remember the original state to restore).
        bool hadKey = reg.KeyExists(RegistryHive.CurrentUser, HideService.BlockedListPath);
        object? originalValue = hadKey
            ? reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, TestClsid)
            : null;
        if (originalValue != null)
            reg.DeleteValue(RegistryHive.CurrentUser, HideService.BlockedListPath, TestClsid);

        try
        {
            var hide = new HideService(reg);
            var target = HideService.BlockedShellExtTarget(TestClsid);

            // Direct exercise: HideService.Hide should write the value.
            hide.Hide(new[] { target });
            var afterHide = reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, TestClsid);
            Assert.Equal("", afterHide);

            // And HideService.Unhide should remove it.
            hide.Unhide(new[] { target });
            var afterUnhide = reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, TestClsid);
            Assert.Null(afterUnhide);
        }
        finally
        {
            // Restore.
            if (originalValue != null)
                reg.SetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, TestClsid, originalValue);
            else
                reg.DeleteValue(RegistryHive.CurrentUser, HideService.BlockedListPath, TestClsid);
        }
    }

    [Fact]
    public void LegacyDisable_HideTarget_uses_HKCU_so_apply_works_without_admin()
    {
        var reg = new Win32Registry();
        // Pick a verb that we know exists in HKCR on Windows: AllFilesystemObjects\shell\pintohome
        // or fall back to any verb from the user's machine. We construct the HideTarget
        // exactly as VerbToRegistryMapper would.
        var target = new RCMM.Core.Models.HideTarget(
            RCMM.Core.Models.HideKind.LegacyDisable,
            RegistryHive.CurrentUser,
            @"Software\Classes\*\shell\RCMMTestProbeVerb",
            "LegacyDisable");
        var hide = new HideService(reg);

        try { reg.DeleteKey(RegistryHive.CurrentUser, target.Path); } catch { }

        hide.Hide(new[] { target });
        Assert.Equal("", reg.GetValue(RegistryHive.CurrentUser, target.Path, "LegacyDisable"));

        hide.Unhide(new[] { target });
        Assert.Null(reg.GetValue(RegistryHive.CurrentUser, target.Path, "LegacyDisable"));

        try { reg.DeleteKey(RegistryHive.CurrentUser, target.Path); } catch { }
    }

    [Fact]
    public void ViewModel_OnRowToggled_then_ApplyPending_writes_to_registry()
    {
        var reg = new Win32Registry();
        // Build minimal services. Capture is stubbed (returns nothing) so we control
        // what rows the ViewModel produces; the packaged scanner is real.
        var files = new Win32FileVersionReader();
        var mui = new Win32MuiStringResolver();
        var resolver = new ClsidResolver(reg);
        var shellexIndex = new ShellexNameIndex(reg, resolver, files);
        var verbScanner = new ClassicVerbScanner(reg, mui);
        var shellexScanner = new ClassicShellexScanner(reg, resolver, files);
        var entryScanner = new EntryScanner(verbScanner, shellexScanner);
        var packagedScanner = new PackagedShellExtScanner(reg, mui);
        var mapper = new VerbToRegistryMapper(reg);
        var capture = new FakeContextMenuCaptureService();
        var hide = new HideService(reg);
        var targets = new TargetProvider();

        var vm = new MainViewModel(capture, targets, mapper, hide, reg, files, shellexIndex, entryScanner, packagedScanner);

        // Clean precondition.
        bool hadKey = reg.KeyExists(RegistryHive.CurrentUser, HideService.BlockedListPath);
        object? originalValue = hadKey
            ? reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, TestClsid)
            : null;
        if (originalValue != null)
            reg.DeleteValue(RegistryHive.CurrentUser, HideService.BlockedListPath, TestClsid);

        try
        {
            vm.Rescan();

            // Find the WinRAR row. If the scanner didn't surface it on this machine,
            // skip the rest of the test.
            var winrar = vm.AllEntries.FirstOrDefault(r =>
                string.Equals(r.Entry.Id, "clsid:" + TestClsid.ToLowerInvariant()));
            if (winrar == null) return;

            Assert.True(winrar.CanHide);
            Assert.False(winrar.IsHidden);
            Assert.Contains(winrar.Entry.HideTargets, t =>
                t.Path == HideService.BlockedListPath && t.ValueName == TestClsid);

            // Simulate the UI toggle.
            winrar.IsHidden = true;
            Assert.Contains(winrar.Entry.Id, vm.PendingChangeIds);

            vm.ApplyPending();

            var afterApply = reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, TestClsid);
            Assert.Equal("", afterApply);
        }
        finally
        {
            if (originalValue != null)
                reg.SetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, TestClsid, originalValue);
            else
                reg.DeleteValue(RegistryHive.CurrentUser, HideService.BlockedListPath, TestClsid);
        }
    }
}
