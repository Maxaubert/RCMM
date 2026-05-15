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

    // ----- helpers for the new tests below -----
    private static AdditionEntry E(string id, string? folderId = null)
        => new AdditionEntry
        {
            Id = id, Name = id, Command = id, WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
            FolderId = folderId,
        };
    private static AdditionFolder F(string id, string? parentId = null)
        => new AdditionFolder { Id = id, Name = id, ParentFolderId = parentId };

    // ----- MoveEntry -----

    [Fact]
    public void MoveEntry_into_folder_sets_FolderId_and_lands_at_end_of_bucket()
    {
        var vm = new AddPageViewModel(new AdditionStore(TempFile()));
        vm.AddFolder(F("f1"));
        vm.AddEntry(E("e1", "f1"));
        vm.AddEntry(E("e2", "f1"));
        vm.AddEntry(E("e3", null));
        // e3 lives at root; move it into f1 → should be the LAST entry in f1's bucket
        vm.MoveEntry("e3", "f1");
        var inF1 = vm.Entries.Where(e => e.FolderId == "f1").Select(e => e.Id).ToList();
        Assert.Equal(new[] { "e1", "e2", "e3" }, inF1);
        Assert.True(vm.HasPendingChanges);
    }

    [Fact]
    public void MoveEntry_to_root_clears_FolderId()
    {
        var vm = new AddPageViewModel(new AdditionStore(TempFile()));
        vm.AddFolder(F("f1"));
        vm.AddEntry(E("e1", "f1"));
        vm.MoveEntry("e1", null);
        Assert.Null(vm.Entries.Single().FolderId);
    }

    [Fact]
    public void MoveEntry_to_same_bucket_is_noop()
    {
        var vm = new AddPageViewModel(new AdditionStore(TempFile()));
        vm.AddEntry(E("e1"));
        vm.MarkClean();
        vm.MoveEntry("e1", null);
        Assert.False(vm.HasPendingChanges);
    }

    // ----- MoveFolder + depth cap -----

    [Fact]
    public void MoveFolder_nests_under_parent_when_depth_allows()
    {
        var vm = new AddPageViewModel(new AdditionStore(TempFile()));
        vm.AddFolder(F("a"));
        vm.AddFolder(F("b"));
        Assert.True(vm.MoveFolder("b", "a"));
        Assert.Equal("a", vm.Folders.Single(f => f.Id == "b").ParentFolderId);
    }

    [Fact]
    public void MoveFolder_refuses_when_depth_cap_would_break()
    {
        var vm = new AddPageViewModel(new AdditionStore(TempFile()));
        vm.AddFolder(F("L1"));
        vm.AddFolder(F("L2", "L1"));
        vm.AddFolder(F("L3", "L2"));
        // Cannot nest another folder under L3 (would be depth 4)
        vm.AddFolder(F("orphan"));
        Assert.False(vm.MoveFolder("orphan", "L3"));
        // Original parent is unchanged
        Assert.Null(vm.Folders.Single(f => f.Id == "orphan").ParentFolderId);
    }

    [Fact]
    public void MoveFolder_refuses_cycle()
    {
        var vm = new AddPageViewModel(new AdditionStore(TempFile()));
        vm.AddFolder(F("a"));
        vm.AddFolder(F("b", "a"));
        // Trying to put 'a' under its own child 'b' → cycle, refused
        Assert.False(vm.MoveFolder("a", "b"));
    }

    [Fact]
    public void MoveFolder_refuses_self_parent()
    {
        var vm = new AddPageViewModel(new AdditionStore(TempFile()));
        vm.AddFolder(F("a"));
        Assert.False(vm.MoveFolder("a", "a"));
    }

    // ----- ReorderEntryWithinBucket -----

    [Fact]
    public void ReorderEntryWithinBucket_moves_to_before_target()
    {
        var vm = new AddPageViewModel(new AdditionStore(TempFile()));
        vm.AddEntry(E("e1"));
        vm.AddEntry(E("e2"));
        vm.AddEntry(E("e3"));
        // Move e3 before e1 → new order: e3, e1, e2
        vm.ReorderEntryWithinBucket("e3", beforeEntryId: "e1");
        Assert.Equal(new[] { "e3", "e1", "e2" }, vm.Entries.Select(e => e.Id));
    }

    [Fact]
    public void ReorderEntryWithinBucket_null_target_moves_to_end()
    {
        var vm = new AddPageViewModel(new AdditionStore(TempFile()));
        vm.AddEntry(E("e1"));
        vm.AddEntry(E("e2"));
        vm.AddEntry(E("e3"));
        vm.ReorderEntryWithinBucket("e1", beforeEntryId: null);
        Assert.Equal(new[] { "e2", "e3", "e1" }, vm.Entries.Select(e => e.Id));
    }

    // ----- Depth helpers -----

    [Fact]
    public void FolderDepth_walks_parent_chain()
    {
        var vm = new AddPageViewModel(new AdditionStore(TempFile()));
        vm.AddFolder(F("L1"));
        vm.AddFolder(F("L2", "L1"));
        vm.AddFolder(F("L3", "L2"));
        Assert.Equal(1, vm.FolderDepth("L1"));
        Assert.Equal(2, vm.FolderDepth("L2"));
        Assert.Equal(3, vm.FolderDepth("L3"));
    }

    [Fact]
    public void MaxSubtreeDepth_counts_deepest_descendant_only()
    {
        var vm = new AddPageViewModel(new AdditionStore(TempFile()));
        vm.AddFolder(F("L1"));
        vm.AddFolder(F("L2", "L1"));
        vm.AddFolder(F("L3", "L2"));
        Assert.Equal(2, vm.MaxSubtreeDepth("L1")); // L1 → L2 → L3, that's 2 levels below L1
        Assert.Equal(1, vm.MaxSubtreeDepth("L2"));
        Assert.Equal(0, vm.MaxSubtreeDepth("L3"));
    }

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
