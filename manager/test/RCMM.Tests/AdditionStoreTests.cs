using System.IO;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Tests;

public class AdditionStoreTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"rcmm-test-{System.Guid.NewGuid():N}.json");

    [Fact]
    public void Load_returns_empty_state_when_file_missing()
    {
        var path = TempFile();
        var store = new AdditionStore(path);
        var state = store.Load();
        Assert.Empty(state.Entries);
        Assert.Empty(state.Folders);
        Assert.Equal(AdditionState.CurrentSchemaVersion, state.SchemaVersion);
    }

    [Fact]
    public void Load_migrates_v1_json_to_current_schema()
    {
        // Hand-crafted v1 JSON — a folder without parentFolderId/scope and one entry.
        // After load it should be v2 with ParentFolderId=null and Scope=FolderBackground.
        var path = TempFile();
        try
        {
            File.WriteAllText(path,
                @"{""schemaVersion"":1,
                   ""folders"":[{""id"":""f1"",""name"":""Dev tools""}],
                   ""entries"":[{""id"":""e1"",""name"":""npm run dev"",""command"":""npm run dev"",""workingDir"":""%V"",""scope"":""folderBackground"",""runMode"":""visibleTerminal""}]}");
            var state = new AdditionStore(path).Load();

            Assert.Equal(AdditionState.CurrentSchemaVersion, state.SchemaVersion);
            Assert.Single(state.Folders);
            Assert.Null(state.Folders[0].ParentFolderId);
            Assert.Equal(AdditionScope.FolderBackground, state.Folders[0].Scope);
            Assert.Single(state.Entries);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Save_then_Load_roundtrips_nested_folder()
    {
        var path = TempFile();
        try
        {
            var store = new AdditionStore(path);
            var parent = new AdditionFolder { Id = "p", Name = "Dev tools", Scope = AdditionScope.FolderBackground };
            var child = new AdditionFolder { Id = "c", Name = "Shells", ParentFolderId = "p", Scope = AdditionScope.FolderBackground };
            store.Save(new AdditionState { Folders = new[] { parent, child } });
            var reloaded = store.Load();

            Assert.Equal(2, reloaded.Folders.Count);
            var reloadedChild = System.Linq.Enumerable.First(reloaded.Folders, f => f.Id == "c");
            Assert.Equal("p", reloadedChild.ParentFolderId);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Save_then_Load_roundtrips_one_entry()
    {
        var path = TempFile();
        try
        {
            var store = new AdditionStore(path);
            var entry = new AdditionEntry
            {
                Id = "abc-123",
                Name = "npm run dev",
                Command = "npm run dev",
                WorkingDir = "%V",
                Scope = AdditionScope.FolderBackground,
                RunMode = RunMode.VisibleTerminal
            };
            store.Save(new AdditionState { Entries = new[] { entry } });
            var reloaded = store.Load();
            Assert.Single(reloaded.Entries);
            Assert.Equal("npm run dev", reloaded.Entries[0].Name);
            Assert.Equal(AdditionScope.FolderBackground, reloaded.Entries[0].Scope);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Save_leaves_no_tmp_file_on_disk()
    {
        var path = TempFile();
        try
        {
            var store = new AdditionStore(path);
            store.Save(new AdditionState());
            Assert.False(File.Exists(path + ".tmp"), "tmp file should have been renamed");
            Assert.True(File.Exists(path), "real file should exist after save");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void Load_with_corrupt_json_returns_empty_state()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{not valid json");
            var store = new AdditionStore(path);
            var state = store.Load();
            Assert.Empty(state.Entries);
            Assert.Empty(state.Folders);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_migrates_v4_to_v5_dropping_hand_authored_entries()
    {
        // e1 has no sourceTemplateId (hand-authored, pre-trim) -> dropped.
        // e2 is template-derived -> survives with hidden/folder/order intact.
        var path = TempFile();
        try
        {
            File.WriteAllText(path,
                @"{""schemaVersion"":4,
                   ""folders"":[{""id"":""f1"",""name"":""Dev tools""}],
                   ""entries"":[
                     {""id"":""e1"",""name"":""My own thing"",""command"":""calc.exe"",""workingDir"":""%V"",""scope"":""folderBackground"",""runMode"":""background""},
                     {""id"":""e2"",""name"":""git pull"",""command"":""git pull"",""workingDir"":""%V"",""scope"":""folderBackground"",""runMode"":""visibleTerminal"",""sourceTemplateId"":""git pull"",""appliedTemplateHash"":""x"",""hidden"":true,""folderId"":""f1""}]}");
            var state = new AdditionStore(path).Load();

            Assert.Equal(5, state.SchemaVersion);
            var survivor = Assert.Single(state.Entries);
            Assert.Equal("e2", survivor.Id);
            Assert.True(survivor.Hidden);
            Assert.Equal("f1", survivor.FolderId);
            Assert.Single(state.Folders);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_migrates_v1_all_the_way_to_v5()
    {
        // v3 stamps "npm run dev" (name + structural match against the built-in
        // template) as template-derived, so v5 keeps it; "My own thing" matches
        // nothing and is dropped. Folders always survive.
        var path = TempFile();
        try
        {
            File.WriteAllText(path,
                @"{""schemaVersion"":1,
                   ""folders"":[{""id"":""f1"",""name"":""Dev tools""}],
                   ""entries"":[
                     {""id"":""e1"",""name"":""npm run dev"",""command"":""npm run dev"",""workingDir"":""%V"",""scope"":""folderBackground"",""runMode"":""visibleTerminal""},
                     {""id"":""e2"",""name"":""My own thing"",""command"":""calc.exe"",""workingDir"":""%V"",""scope"":""folderBackground"",""runMode"":""background""}]}");
            var state = new AdditionStore(path).Load();

            Assert.Equal(5, state.SchemaVersion);
            var survivor = Assert.Single(state.Entries);
            Assert.Equal("e1", survivor.Id);
            Assert.Equal("npm run dev", survivor.SourceTemplateId);
            Assert.Single(state.Folders);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_out_dropped_true_when_v4_has_only_hand_authored_entries()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path,
                @"{""schemaVersion"":4,
                   ""entries"":[
                     {""id"":""e1"",""name"":""My own thing"",""command"":""calc.exe"",""workingDir"":""%V"",""scope"":""folderBackground"",""runMode"":""background""}]}");
            var state = new AdditionStore(path).Load(out var dropped);

            Assert.True(dropped);
            Assert.Empty(state.Entries);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_out_dropped_false_when_v4_entry_is_template_derived()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path,
                @"{""schemaVersion"":4,
                   ""entries"":[
                     {""id"":""e2"",""name"":""git pull"",""command"":""git pull"",""workingDir"":""%V"",""scope"":""folderBackground"",""runMode"":""visibleTerminal"",""sourceTemplateId"":""git pull"",""appliedTemplateHash"":""x""}]}");
            var state = new AdditionStore(path).Load(out var dropped);

            Assert.False(dropped);
            Assert.Single(state.Entries);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_out_dropped_false_when_file_missing()
    {
        var path = TempFile();
        var state = new AdditionStore(path).Load(out var dropped);
        Assert.False(dropped);
        Assert.Empty(state.Entries);
    }

    [Fact]
    public void Load_out_dropped_false_when_json_corrupt()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{not valid json");
            var state = new AdditionStore(path).Load(out var dropped);
            Assert.False(dropped);
            Assert.Empty(state.Entries);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
