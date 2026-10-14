using System.Collections.Generic;
using System.IO;
using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;
using Xunit;

namespace RCMM.Tests;

/// <summary>
/// Regression tests for the review b04 discovery-filter findings: the visible
/// list must trust real menu text (live captures, GetTitle) even when it looks
/// like a CamelCase identifier, must keep hidden rows recoverable, and must not
/// depend on English display literals to suppress informational probe rows.
/// </summary>
public class MainViewModelDiscoveryFilterTests : System.IDisposable
{
    private readonly string _tempRoot;
    private readonly TargetProvider _targets;

    public MainViewModelDiscoveryFilterTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"rcmm-mvmf-{System.Guid.NewGuid():N}");
        _targets = new TargetProvider(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private (MainViewModel vm, FakeRegistry reg, FakeContextMenuCaptureService cap, FakeFileVersionReader files)
        BuildSut(bool withRegistryScanner = false, FakeShellexInvoker? invoker = null)
    {
        var reg = new FakeRegistry();
        var cap = new FakeContextMenuCaptureService();
        var mapper = new VerbToRegistryMapper(reg);
        var hide = new HideService(reg);
        var files = new FakeFileVersionReader();
        var resolver = new ClsidResolver(reg);
        var shellexIndex = new ShellexNameIndex(reg, resolver, files);
        var scanner = withRegistryScanner
            ? new EntryScanner(
                new ClassicVerbScanner(reg, new FakeMuiStringResolver()),
                new ClassicShellexScanner(reg, resolver, files))
            : null;
        var vm = new MainViewModel(cap, _targets, mapper, hide, reg, files, shellexIndex,
            registryScanner: scanner,
            shellexInvoker: invoker,
            knownStore: new KnownEntriesStore(Path.Combine(_tempRoot, "known-entries.json")));
        return (vm, reg, cap, files);
    }

    private string FirstFileTarget() => _targets.GetTargets().First(p => p.EndsWith(".txt"));

    // ---- Finding 2: live-captured CamelCase brand names must survive ----

    [Fact]
    public void Live_captured_CamelCase_brand_verb_survives_the_technical_name_filter()
    {
        var (vm, reg, cap, _) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\teracopy", "", "TeraCopy...");

        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            // What Explorer renders for TeraCopy: a single-token mixed-case
            // brand name that trips the CamelCase identifier heuristic.
            new() { TargetPath = target, Position = 0, DisplayName = "TeraCopy...", Verb = "teracopy" }
        };

        vm.Rescan();

        var row = Assert.Single(vm.AllEntries);
        Assert.Equal("TeraCopy...", row.DisplayName);
        Assert.True(row.CanHide);
    }

    [Fact]
    public void Live_captured_CamelCase_shellex_row_survives_the_technical_name_filter()
    {
        var (vm, reg, cap, _) = BuildSut();
        var clsid = "{0E270DAA-1BE6-48F2-AC49-00000000AAAA}";
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\PowerRename", "", clsid);

        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "PowerRename", OwnerClsid = clsid }
        };

        vm.Rescan();

        var row = Assert.Single(vm.AllEntries);
        Assert.Equal("PowerRename", row.DisplayName);
    }

    [Fact]
    public void Registry_derived_CamelCase_fallback_name_is_still_filtered()
    {
        // The heuristic keeps judging names WITHOUT menu-text provenance: a
        // registry-scanned verb whose display value is an identifier-looking
        // string, never seen in any live capture and not hidden, stays out.
        var (vm, reg, _, _) = BuildSut(withRegistryScanner: true);
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\SuperTool", "", "SuperToolLauncher");

        vm.Rescan();

        Assert.DoesNotContain(vm.AllEntries, r => r.Entry.DisplayName == "SuperToolLauncher");
    }

    // ---- Finding 34: key-name-only verbs are discovered; pre-hidden ones recoverable ----

    [Fact]
    public void Pre_hidden_key_name_only_verb_is_discovered_and_recoverable()
    {
        // HKCR\Directory\shell\OpenWithVSCode has no default value and no
        // MUIVerb (display = key name) and carries a LegacyDisable written by
        // an external tweak script before RCMM ever scanned. The live capture
        // cannot see a disabled verb, so the registry scan is the only path —
        // the row must appear (hidden) so the user can un-hide it.
        var (vm, reg, _, _) = BuildSut(withRegistryScanner: true);
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\OpenWithVSCode", "LegacyDisable", "");

        vm.Rescan();

        var row = vm.AllEntries.Single(r => r.Entry.Id == "verb:openwithvscode");
        Assert.Equal("OpenWithVSCode", row.DisplayName);
        Assert.True(row.Entry.IsHidden);
        Assert.True(row.CanHide);
    }

    [Fact]
    public void Visible_key_name_only_verb_with_friendly_key_name_is_listed()
    {
        var (vm, reg, _, _) = BuildSut(withRegistryScanner: true);
        // No default value; the key name itself is what Explorer renders, and
        // it doesn't look like an identifier, so the row is offered as-is.
        reg.CreateKey(RegistryHive.ClassesRoot, @"Directory\shell\Backup");

        vm.Rescan();

        Assert.Contains(vm.AllEntries, r => r.Entry.Id == "verb:backup" && r.DisplayName == "Backup");
    }

    // ---- Finding 33: stable identifiers over English literals ----

    [Fact]
    public void Create_shortcut_is_forced_builtin_by_canonical_verb_id_when_text_is_localized()
    {
        var (vm, _, cap, _) = BuildSut();
        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            // German Windows: the menu text doesn't match the English literal,
            // but the canonical verb id "link" is locale-independent.
            new() { TargetPath = target, Position = 0, DisplayName = "Verknüpfung erstellen", Verb = "link" }
        };

        vm.Rescan();

        var row = Assert.Single(vm.AllEntries);
        Assert.True(row.Entry.IsBuiltIn);
    }

    [Fact]
    public void Emission_only_renamed_unobserved_clsid_row_is_filtered_without_english_literals()
    {
        // A conditional/informational handler (Work Folders, Sync Center, …)
        // emits localized menu text during the COM probe but never contributes
        // to a real menu. The row must be suppressed by the structural
        // condition (emission-only rename + never observed live), not by an
        // English display-name literal that a localized emission would dodge.
        var (vm, reg, _, files) = BuildSut(withRegistryScanner: true, invoker: new FakeShellexInvoker
        {
            EmittedByClsid = { ["{ABCDEF00-1111-2222-3333-444444444444}"] = new List<string> { "Synchronisierungscenter wird gestartet" } }
        });
        var clsid = "{ABCDEF00-1111-2222-3333-444444444444}";
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shellex\ContextMenuHandlers\SyncUi", "", clsid);
        reg.SetValue(RegistryHive.ClassesRoot, $@"CLSID\{clsid}\InprocServer32", "", @"C:\Windows\System32\syncui.dll");
        files.Map[@"C:\Windows\System32\syncui.dll"] = new FileVersion("SyncCenterHandler", "Microsoft Corporation", null);

        vm.Rescan();

        Assert.DoesNotContain(vm.AllEntries,
            r => r.Entry.DisplayName == "Synchronisierungscenter wird gestartet");
    }

    [Fact]
    public void Static_override_renamed_clsid_row_stays_visible_even_when_unobserved()
    {
        // The curated override table (Defender, NVIDIA) is a trusted rename
        // source: those handlers refuse to probe, so their rows are never
        // "observed" — they must still appear under their real menu text.
        var (vm, reg, _, files) = BuildSut(withRegistryScanner: true, invoker: new FakeShellexInvoker());
        var clsid = "{ABCDEF00-5555-6666-7777-888888888888}";
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\EPP", "", clsid);
        reg.SetValue(RegistryHive.ClassesRoot, $@"CLSID\{clsid}\InprocServer32", "", @"C:\Windows\System32\shellext.dll");
        files.Map[@"C:\Windows\System32\shellext.dll"] =
            new FileVersion("Microsoft Security Client Shell Extension", "Microsoft Corporation", null);

        vm.Rescan();

        Assert.Contains(vm.AllEntries, r => r.Entry.DisplayName == "Scan with Microsoft Defender");
    }

    [Fact]
    public void Hidden_registry_shellex_renamed_from_CamelCase_emission_is_recoverable()
    {
        // A handler whose true menu text is a CamelCase brand name and that is
        // currently blocked (so absent from live capture): the emission rename
        // plus the hidden-row exemption keep it in the list for un-hiding.
        var clsid = "{ABCDEF00-9999-AAAA-BBBB-CCCCCCCCCCCC}";
        var (vm, reg, _, files) = BuildSut(withRegistryScanner: true, invoker: new FakeShellexInvoker
        {
            EmittedByClsid = { [clsid] = new List<string> { "TeraCopy..." } }
        });
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\TeraCopy", "", clsid);
        reg.SetValue(RegistryHive.ClassesRoot, $@"CLSID\{clsid}\InprocServer32", "", @"C:\Tools\teracopyext.dll");
        files.Map[@"C:\Tools\teracopyext.dll"] = new FileVersion("TeraCopyShellExtension", "Code Sector", null);
        // The handler is currently masked (hidden) via the HKCU shadow key.
        reg.CreateKey(RegistryHive.CurrentUser, @"Software\Classes\*\shellex\ContextMenuHandlers\TeraCopy");

        vm.Rescan();

        var row = vm.AllEntries.Single(r => r.Entry.DisplayName == "TeraCopy...");
        Assert.True(row.Entry.IsHidden);
    }
}
