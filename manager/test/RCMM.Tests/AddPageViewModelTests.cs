using System.IO;
using System.Linq;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;

namespace RCMM.Tests;

public class AddPageViewModelTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"rcmm-vm-{System.Guid.NewGuid():N}.json");

    [Fact]
    public void Load_from_empty_store_yields_empty_collections()
    {
        var store = new AdditionStore(TempFile());
        var vm = new AddPageViewModel(store);
        vm.Load();
        Assert.Empty(vm.Entries);
        Assert.Empty(vm.Folders);
        Assert.False(vm.HasPendingChanges);
    }

    [Fact]
    public void AddEntry_appends_and_marks_pending()
    {
        var store = new AdditionStore(TempFile());
        var vm = new AddPageViewModel(store);
        var entry = new AdditionEntry
        {
            Id = "e1", Name = "Test", Command = "x", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
        };
        vm.AddEntry(entry);
        Assert.Single(vm.Entries);
        Assert.True(vm.HasPendingChanges);
    }

    [Fact]
    public void DeleteEntry_removes_and_marks_pending()
    {
        var store = new AdditionStore(TempFile());
        var vm = new AddPageViewModel(store);
        var entry = new AdditionEntry
        {
            Id = "e1", Name = "x", Command = "x", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
        };
        vm.AddEntry(entry);
        vm.DeleteEntry("e1");
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public void ReplaceEntry_updates_in_place()
    {
        var store = new AdditionStore(TempFile());
        var vm = new AddPageViewModel(store);
        var entry = new AdditionEntry
        {
            Id = "e1", Name = "old", Command = "x", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
        };
        vm.AddEntry(entry);
        vm.ReplaceEntry(entry with { Name = "new" });
        Assert.Equal("new", vm.Entries.Single().Name);
    }

    [Fact]
    public void DeleteFolder_orphans_its_entries_to_top_level()
    {
        var store = new AdditionStore(TempFile());
        var vm = new AddPageViewModel(store);
        var folder = new AdditionFolder { Id = "f", Name = "F" };
        vm.AddFolder(folder);
        vm.AddEntry(new AdditionEntry
        {
            Id = "e", Name = "child", Command = "x", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
            FolderId = "f",
        });
        vm.DeleteFolder("f");
        Assert.Empty(vm.Folders);
        Assert.Null(vm.Entries.Single().FolderId);
    }

    [Fact]
    public void Snapshot_returns_AdditionState_of_current_buffer()
    {
        var store = new AdditionStore(TempFile());
        var vm = new AddPageViewModel(store);
        vm.AddFolder(new AdditionFolder { Id = "f", Name = "F" });
        vm.AddEntry(new AdditionEntry
        {
            Id = "e", Name = "x", Command = "x", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
        });
        var state = vm.Snapshot();
        Assert.Single(state.Folders);
        Assert.Single(state.Entries);
    }

    [Fact]
    public void MarkClean_resets_pending_flag()
    {
        var store = new AdditionStore(TempFile());
        var vm = new AddPageViewModel(store);
        vm.AddEntry(new AdditionEntry
        {
            Id = "e", Name = "x", Command = "x", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
        });
        Assert.True(vm.HasPendingChanges);
        vm.MarkClean();
        Assert.False(vm.HasPendingChanges);
    }
}
