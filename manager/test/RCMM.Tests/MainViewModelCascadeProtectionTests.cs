using System;
using System.IO;
using System.Linq;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;
using Xunit;

namespace RCMM.Tests;

/// <summary>
/// Exercises the cascade-protection ORCHESTRATION in MainViewModel.ApplyPendingCore
/// — the wiring the service-level CascadeProtectionServiceTests never touch. All
/// registry access goes through FakeRegistry; the packaged-COM scanner is fed a
/// synthetic PackagedCom hive plus on-disk AppxManifest.xml files in a temp dir,
/// so Rescan populates _backgroundExtsByClsid exactly the way it does in
/// production, without reading any real machine state.
/// </summary>
public sealed class MainViewModelCascadeProtectionTests : IDisposable
{
    private const string AmdClsid = "{6767B3BC-8FF7-11EC-B909-0242AC120002}";
    private const string TermClsid = "{9F156763-7844-4DC4-B2B1-901F640F5155}";
    private const string CodeClsid = "{33333333-3333-3333-3333-333333333333}";
    private const string BgShell = "Software\\Classes\\Directory\\Background\\shell";

    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "rcmm-cascade-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static string VerbPath(string clsid)
        => BgShell + "\\RcmmProtect_" + clsid.Trim('{', '}');

    /// <summary>
    /// Registers a fake packaged-COM Background extension: the PackagedCom Server
    /// entry the scanner enumerates, the AppxAllUserStore Path that resolves the
    /// install folder, and an AppxManifest.xml binding the CLSID to
    /// Directory\Background (which is also where the AUMID comes from).
    /// </summary>
    private void SeedBackgroundPackage(FakeRegistry reg, string pkgFullName, string clsid, string appDisplayName)
    {
        var pkgDir = Path.Combine(_tempDir, pkgFullName);
        Directory.CreateDirectory(pkgDir);
        File.WriteAllText(Path.Combine(pkgDir, "AppxManifest.xml"), $@"<Package>
  <Applications>
    <Application Id=""App"">
      <Extensions>
        <Extension Category=""windows.fileExplorerContextMenus"">
          <FileExplorerContextMenus>
            <ItemType Type=""Directory\Background"">
              <Verb Id=""Cmd"" Clsid=""{clsid}"" />
            </ItemType>
          </FileExplorerContextMenus>
        </Extension>
      </Extensions>
    </Application>
  </Applications>
</Package>");
        var server = $"SOFTWARE\\Classes\\PackagedCom\\Package\\{pkgFullName}\\Server\\0";
        reg.SetValue(RegistryHive.LocalMachine, server, "SurrogateAppId", clsid);
        // Friendly, space-containing names so the step-9 technical-name filter
        // keeps the rows visible (raw class names like "WindowsTerminalShellExt"
        // would be filtered and leave nothing to toggle).
        reg.SetValue(RegistryHive.LocalMachine, server, "DisplayName", appDisplayName);
        reg.SetValue(RegistryHive.LocalMachine, server, "ApplicationDisplayName", appDisplayName);
        reg.SetValue(RegistryHive.LocalMachine,
            $"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Appx\\AppxAllUserStore\\Applications\\{pkgFullName}",
            "Path", Path.Combine(pkgDir, "AppxManifest.xml"));
    }

    private (MainViewModel vm, FakeRegistry reg) BuildSut()
    {
        var reg = new FakeRegistry();
        SeedBackgroundPackage(reg, "AmdSoftware_1.0.0.0_x64__amdhash", AmdClsid, "AMD Software");
        SeedBackgroundPackage(reg, "WindowsTerminal_1.0.0.0_x64__termhash", TermClsid, "Windows Terminal");
        SeedBackgroundPackage(reg, "VsCode_1.0.0.0_x64__codehash", CodeClsid, "VS Code");

        var files = new FakeFileVersionReader();
        var resolver = new ClsidResolver(reg);
        var shellexIndex = new ShellexNameIndex(reg, resolver, files);
        var vm = new MainViewModel(
            new FakeContextMenuCaptureService(), new TargetProvider(), new VerbToRegistryMapper(reg),
            new HideService(reg), reg, files, shellexIndex,
            packagedScanner: new PackagedShellExtScanner(reg),
            cascadeProtector: new CascadeProtectionService(reg),
            knownStore: new KnownEntriesStore(Path.Combine(_tempDir, "known-entries.json")));
        vm.Rescan();
        return (vm, reg);
    }

    private static EntryRowViewModel Row(MainViewModel vm, string clsid)
        => vm.AllEntries.First(r => r.Entry.Id == "clsid:" + clsid.ToLowerInvariant());

    [Fact]
    public void Hiding_first_background_extension_installs_fallbacks_for_the_others_only()
    {
        var (vm, reg) = BuildSut();

        Row(vm, AmdClsid).IsHidden = true;
        vm.ApplyPending();

        // The hide itself landed in the Blocked list…
        Assert.NotNull(reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, AmdClsid));
        // …and the two OTHER Background extensions got classic-verb fallbacks,
        // while the hidden one got none.
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, VerbPath(TermClsid)));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, VerbPath(CodeClsid)));
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser, VerbPath(AmdClsid)));
    }

    [Fact]
    public void Hiding_a_second_extension_later_does_not_resurrect_the_first()
    {
        var (vm, reg) = BuildSut();

        Row(vm, AmdClsid).IsHidden = true;
        vm.ApplyPending();
        Row(vm, TermClsid).IsHidden = true;
        vm.ApplyPending();

        Assert.NotNull(reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, AmdClsid));
        Assert.NotNull(reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, TermClsid));
        // The regression this guards: the second Apply used to plan a fallback
        // for AMD (already hidden, so its verb never existed) — a rendered menu
        // row resurrecting an entry the user explicitly hid.
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser, VerbPath(AmdClsid)));
        // Terminal's own verb (installed by the first Apply) is now stale — it
        // must be removed, not left rendering the freshly hidden extension.
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser, VerbPath(TermClsid)));
        // The still-visible third extension keeps its protection.
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, VerbPath(CodeClsid)));
    }

    [Fact]
    public void Hiding_two_extensions_in_one_batch_installs_no_fallback_for_either()
    {
        var (vm, reg) = BuildSut();

        Row(vm, AmdClsid).IsHidden = true;
        Row(vm, TermClsid).IsHidden = true;
        vm.ApplyPending();

        Assert.False(reg.KeyExists(RegistryHive.CurrentUser, VerbPath(AmdClsid)));
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser, VerbPath(TermClsid)));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, VerbPath(CodeClsid)));
    }

    [Fact]
    public void Unhide_sweeps_protections_only_after_no_background_extension_remains_blocked()
    {
        var (vm, reg) = BuildSut();
        // A user-authored classic verb that must survive every sweep.
        var userVerb = BgShell + "\\OpenInTerminal";
        reg.SetValue(RegistryHive.CurrentUser, userVerb, "", "Open in &Terminal");

        Row(vm, AmdClsid).IsHidden = true;
        vm.ApplyPending();
        Row(vm, TermClsid).IsHidden = true;
        vm.ApplyPending();

        // Unhide AMD while Terminal stays blocked: the cascade can still trigger,
        // so the remaining protection (VS Code's) must NOT be swept yet.
        Row(vm, AmdClsid).IsHidden = false;
        vm.ApplyPending();
        Assert.Null(reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, AmdClsid));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, VerbPath(CodeClsid)));

        // Unhide Terminal too: nothing Background-scoped remains blocked, so every
        // RcmmProtect_ verb is swept — and only those.
        Row(vm, TermClsid).IsHidden = false;
        vm.ApplyPending();
        Assert.Null(reg.GetValue(RegistryHive.CurrentUser, HideService.BlockedListPath, TermClsid));
        Assert.DoesNotContain(
            reg.GetSubKeyNames(RegistryHive.CurrentUser, BgShell),
            n => n.StartsWith(CascadeProtectionService.VerbPrefix));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, userVerb));
    }
}
