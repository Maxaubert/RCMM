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

            Assert.Equal(AdditionState.CurrentSchemaVersion, state.SchemaVersion);
            var survivor = Assert.Single(state.Entries);
            Assert.Equal("e2", survivor.Id);
            Assert.True(survivor.Hidden);
            Assert.Equal("f1", survivor.FolderId);
            // v6 re-keys the Name-based source id to the template's stable slug.
            Assert.Equal("git-pull", survivor.SourceTemplateId);
            Assert.Single(state.Folders);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_migrates_v1_all_the_way_to_current()
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

            Assert.Equal(AdditionState.CurrentSchemaVersion, state.SchemaVersion);
            var survivor = Assert.Single(state.Entries);
            Assert.Equal("e1", survivor.Id);
            // v3 stamps the stable slug id directly (v6 scheme).
            Assert.Equal("npm-run-dev", survivor.SourceTemplateId);
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

    // ----- absent-vs-unreadable distinction (data-loss guard) -----

    [Fact]
    public void Load_flags_failure_and_backs_up_file_when_json_corrupt()
    {
        // A file that exists but can't be parsed must (a) be flagged so Apply can
        // refuse to purge based on the empty stand-in state, and (b) be copied to
        // .bad before any Save can overwrite the only copy of the user's data.
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{not valid json");
            var state = new AdditionStore(path).Load(out _, out var loadFailed);

            Assert.True(loadFailed);
            Assert.Empty(state.Entries);
            Assert.True(File.Exists(path + ".bad"), ".bad backup should exist");
            Assert.Equal("{not valid json", File.ReadAllText(path + ".bad"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bad")) File.Delete(path + ".bad");
        }
    }

    [Fact]
    public void Load_does_not_flag_failure_when_file_missing()
    {
        var path = TempFile();
        var state = new AdditionStore(path).Load(out _, out var loadFailed);
        Assert.False(loadFailed);
        Assert.Empty(state.Entries);
        Assert.False(File.Exists(path + ".bad"));
    }

    // ----- schemaVersion default + v6 identity migration -----

    [Fact]
    public void Load_treats_missing_schemaVersion_as_oldest_schema_and_runs_migrations()
    {
        // Regression: SchemaVersion used to default to CurrentSchemaVersion when
        // the JSON field was absent (hand-edited file), silently skipping every
        // migration. The hand-authored entry below is only dropped if the v5
        // migration actually ran.
        var path = TempFile();
        try
        {
            File.WriteAllText(path,
                @"{""entries"":[
                     {""id"":""e1"",""name"":""My own thing"",""command"":""calc.exe"",""workingDir"":""%V"",""scope"":""folderBackground"",""runMode"":""background""}]}");
            var state = new AdditionStore(path).Load();

            Assert.Equal(AdditionState.CurrentSchemaVersion, state.SchemaVersion);
            Assert.Empty(state.Entries);   // proof the v5 drop ran
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_migrates_v5_name_based_source_ids_to_stable_slugs()
    {
        // v5 documents key SourceTemplateId by template display Name; v6 re-keys
        // to the stable slug Id so renaming a template can't sever tracking.
        // An id matching no current template (removed template) is left as-is.
        var path = TempFile();
        try
        {
            File.WriteAllText(path,
                @"{""schemaVersion"":5,
                   ""entries"":[
                     {""id"":""e1"",""name"":""PowerShell here"",""command"":""wt -d x powershell"",""workingDir"":""%V"",""scope"":""folderBackground"",""runMode"":""background"",""sourceTemplateId"":""PowerShell here"",""appliedTemplateHash"":""x""},
                     {""id"":""e2"",""name"":""Gone"",""command"":""x"",""workingDir"":""%V"",""scope"":""folderBackground"",""runMode"":""background"",""sourceTemplateId"":""Removed template"",""appliedTemplateHash"":""x""}]}");
            var state = new AdditionStore(path).Load();

            Assert.Equal(AdditionState.CurrentSchemaVersion, state.SchemaVersion);
            Assert.Equal("powershell-here", System.Linq.Enumerable.Single(state.Entries, e => e.Id == "e1").SourceTemplateId);
            Assert.Equal("Removed template", System.Linq.Enumerable.Single(state.Entries, e => e.Id == "e2").SourceTemplateId);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
