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
        Assert.Equal(1, state.SchemaVersion);
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
}
