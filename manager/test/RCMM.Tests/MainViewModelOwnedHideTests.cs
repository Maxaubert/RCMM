using System;
using System.IO;
using System.Linq;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;

namespace RCMM.Tests;

/// <summary>
/// Hiding an RCMM-added entry must persist into additions.json rather than being
/// written as a bare LegacyDisable value, because Apply's PurgeOwnedKeys tears
/// every RCMM.-prefixed key down and rewrites it from that file. Issue #10.
/// </summary>
public class MainViewModelOwnedHideTests : IDisposable
{
    private const string OwnedVerb = "RCMM.001.abc";
    private const string OwnedKeyHkcu = "Software\\Classes\\Directory\\Background\\shell\\" + OwnedVerb;

    private readonly string _storePath =
        Path.Combine(Path.GetTempPath(), $"rcmm-test-{Guid.NewGuid():N}", "additions.json");

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_storePath)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Hiding_an_owned_entry_persists_to_the_store_and_survives_a_second_Apply()
    {
        var (vm, addPage, reg) = BuildSut();

        vm.Rescan();
        var row = vm.AllEntries.Single(r => r.Entry.DisplayName == "My Entry");
        row.IsHidden = true;
        vm.ApplyPending();

        // The intent landed in the store, not just the registry...
        Assert.True(addPage.Entries.Single(e => e.Id == "abc").Hidden);
        // ...and the applier re-emitted the marker after its own purge.
        Assert.Equal("", reg.GetValue(RegistryHive.CurrentUser, OwnedKeyHkcu, "LegacyDisable"));

        // The regression: any later Apply purges + rewrites owned keys. Before the
        // fix this silently un-hid the entry.
        vm.ApplyPending();
        Assert.Equal("", reg.GetValue(RegistryHive.CurrentUser, OwnedKeyHkcu, "LegacyDisable"));
    }

    [Fact]
    public void Unhiding_an_owned_entry_clears_the_flag_and_the_marker()
    {
        var (vm, addPage, reg) = BuildSut(startHidden: true);

        vm.Rescan();
        var row = vm.AllEntries.Single(r => r.Entry.DisplayName == "My Entry");
        row.IsHidden = false;
        vm.ApplyPending();

        Assert.False(addPage.Entries.Single(e => e.Id == "abc").Hidden);
        Assert.Null(reg.GetValue(RegistryHive.CurrentUser, OwnedKeyHkcu, "LegacyDisable"));
    }

    /// <summary>A verb RCMM does not own must still take the HideService path.</summary>
    [Fact]
    public void Hiding_a_foreign_verb_still_writes_a_registry_marker()
    {
        var (vm, addPage, reg) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, "Directory\\Background\\shell\\Tabby", "", "Open Tabby here");

        vm.Rescan();
        var row = vm.AllEntries.Single(r => r.Entry.DisplayName == "Open Tabby here");
        row.IsHidden = true;
        vm.ApplyPending();

        Assert.Equal("", reg.GetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\Tabby", "LegacyDisable"));
        // Foreign verbs never touch the additions store.
        Assert.Single(addPage.Entries);
        Assert.False(addPage.Entries[0].Hidden);
    }

    private (MainViewModel, AddPageViewModel, FakeRegistry) BuildSut(bool startHidden = false)
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
        if (startHidden)
        {
            reg.SetValue(RegistryHive.ClassesRoot, "Directory\\Background\\shell\\" + OwnedVerb, "LegacyDisable", "");
            reg.SetValue(RegistryHive.CurrentUser, OwnedKeyHkcu, "LegacyDisable", "");
        }

        var addPage = new AddPageViewModel(new AdditionStore(_storePath));
        addPage.AddEntry(new AdditionEntry
        {
            Id = "abc", Name = "My Entry", Command = "npm run dev", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
            Hidden = startHidden,
        });
        addPage.MarkClean();

        var vm = new MainViewModel(
            new FakeContextMenuCaptureService(), new TargetProvider(), new VerbToRegistryMapper(reg),
            new HideService(reg), reg, files, shellexIndex,
            registryScanner: entryScanner,
            addPage: addPage,
            additionApplier: new AdditionApplier(reg));

        return (vm, addPage, reg);
    }
}
