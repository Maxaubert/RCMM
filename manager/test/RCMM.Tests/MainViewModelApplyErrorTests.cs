using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;

namespace RCMM.Tests;

/// <summary>
/// ApplyPending must report failures to its caller instead of burying them in
/// rcmm.log while the UI un-badges everything and restarts Explorer as if the
/// apply succeeded — and it must refuse to apply additions from a stub state
/// when additions.json failed to load (that Apply would purge the user's real
/// registry entries). Review findings b07 #17 / #6.
/// </summary>
public class MainViewModelApplyErrorTests : IDisposable
{
    /// <summary>Delegates to a FakeRegistry, but throws on SetValue for paths
    /// matching a predicate — simulates HKCU being transiently unwritable.</summary>
    private sealed class ThrowingRegistry : IRegistry
    {
        private readonly FakeRegistry _inner;
        public Func<string, bool>? FailSetValueWhen { get; set; }
        public ThrowingRegistry(FakeRegistry inner) { _inner = inner; }

        public bool KeyExists(RegistryHive hive, string path) => _inner.KeyExists(hive, path);
        public void CreateKey(RegistryHive hive, string path) => _inner.CreateKey(hive, path);
        public void DeleteKey(RegistryHive hive, string path) => _inner.DeleteKey(hive, path);
        public void DeleteValue(RegistryHive hive, string path, string name) => _inner.DeleteValue(hive, path, name);
        public object? GetValue(RegistryHive hive, string path, string name) => _inner.GetValue(hive, path, name);
        public void SetValue(RegistryHive hive, string path, string name, object value)
        {
            if (FailSetValueWhen?.Invoke(path) == true)
                throw new UnauthorizedAccessException($"simulated write failure at {path}");
            _inner.SetValue(hive, path, name, value);
        }
        public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string path) => _inner.GetSubKeyNames(hive, path);
        public IReadOnlyList<string> GetValueNames(RegistryHive hive, string path) => _inner.GetValueNames(hive, path);
    }

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"rcmm-applyerr-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private (MainViewModel vm, AddPageViewModel addPage, FakeRegistry fake, ThrowingRegistry reg) BuildSut(
        string? additionsJson = null)
    {
        Directory.CreateDirectory(_dir);
        var storePath = Path.Combine(_dir, "additions.json");
        if (additionsJson != null) File.WriteAllText(storePath, additionsJson);

        var fake = new FakeRegistry();
        var reg = new ThrowingRegistry(fake);
        var files = new FakeFileVersionReader();
        var mui = new FakeMuiStringResolver();
        var resolver = new ClsidResolver(reg);
        var shellexIndex = new ShellexNameIndex(reg, resolver, files);
        var entryScanner = new EntryScanner(
            new ClassicVerbScanner(reg, mui),
            new ClassicShellexScanner(reg, resolver, files));

        var addPage = new AddPageViewModel(new AdditionStore(storePath));
        addPage.Load();

        var vm = new MainViewModel(
            new FakeContextMenuCaptureService(), new TargetProvider(), new VerbToRegistryMapper(reg),
            new HideService(reg), reg, files, shellexIndex,
            registryScanner: entryScanner,
            addPage: addPage,
            additionApplier: new AdditionApplier(reg),
            knownStore: new KnownEntriesStore(Path.Combine(_dir, "known-entries.json")));
        return (vm, addPage, fake, reg);
    }

    [Fact]
    public void ApplyPending_refuses_additions_when_the_store_failed_to_load()
    {
        // additions.json exists but is unreadable → the page buffer is a stub.
        var (vm, addPage, fake, _) = BuildSut(additionsJson: "{not valid json");
        Assert.True(addPage.LoadFailed);

        // Simulate a pre-existing owned verb the purge would destroy.
        const string ownedKey = "Software\\Classes\\Directory\\Background\\shell\\RCMM.001.abc";
        fake.SetValue(RegistryHive.CurrentUser, ownedKey, "", "My Entry");

        // The user, seeing an empty page, adds something and applies.
        addPage.AddEntry(new AdditionEntry
        {
            Id = "new1", Name = "New", Command = "x", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
        });
        var errors = vm.ApplyPending();

        Assert.Contains(errors, e => e.Contains("additions.json"));
        // The refusal protected the real data: no purge, no JSON overwrite, still pending.
        Assert.True(fake.KeyExists(RegistryHive.CurrentUser, ownedKey));
        Assert.Equal("{not valid json", File.ReadAllText(Path.Combine(_dir, "additions.json")));
        Assert.True(addPage.HasPendingChanges);
    }

    [Fact]
    public void ApplyPending_reports_a_failed_additions_write_and_keeps_the_page_dirty()
    {
        var (vm, addPage, _, reg) = BuildSut();
        addPage.AddEntry(new AdditionEntry
        {
            Id = "new1", Name = "New", Command = "x", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
        });
        reg.FailSetValueWhen = path => path.Contains("RCMM.");

        var errors = vm.ApplyPending();

        Assert.Contains(errors, e => e.Contains("added entries"));
        Assert.True(addPage.HasPendingChanges, "MarkClean must not run after a failed apply");
        // The JSON was not saved over: a failed registry write leaves the store on
        // its previous (here: absent) state.
        Assert.False(File.Exists(Path.Combine(_dir, "additions.json")));
    }

    [Fact]
    public void ApplyPending_reports_a_failed_hide_and_requeues_it_for_retry()
    {
        var (vm, _, fake, reg) = BuildSut();
        fake.SetValue(RegistryHive.ClassesRoot, "Directory\\Background\\shell\\Tabby", "", "Open Tabby here");

        vm.Rescan();
        var row = vm.AllEntries.Single(r => r.Entry.DisplayName == "Open Tabby here");
        row.IsHidden = true;
        reg.FailSetValueWhen = path => path.Contains("Tabby");

        var errors = vm.ApplyPending();

        Assert.NotEmpty(errors);
        // The failed hide keeps its badge and stays queued...
        Assert.Contains(row.Entry.Id, vm.PendingChangeIds);
        // ...so once the transient failure clears, the next Apply lands it.
        reg.FailSetValueWhen = null;
        Assert.Empty(vm.ApplyPending());
        Assert.Equal("", fake.GetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\Tabby", "LegacyDisable"));
        Assert.DoesNotContain(row.Entry.Id, vm.PendingChangeIds);
    }

    [Fact]
    public void ApplyPending_returns_no_errors_on_a_clean_apply()
    {
        var (vm, _, fake, _) = BuildSut();
        fake.SetValue(RegistryHive.ClassesRoot, "Directory\\Background\\shell\\Tabby", "", "Open Tabby here");

        vm.Rescan();
        var row = vm.AllEntries.Single(r => r.Entry.DisplayName == "Open Tabby here");
        row.IsHidden = true;

        Assert.Empty(vm.ApplyPending());
    }
}
