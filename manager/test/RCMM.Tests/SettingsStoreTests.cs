using System;
using System.IO;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), "rcmm-settings-test-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
        try { File.Delete(_path + ".tmp"); } catch { }
        try { File.Delete(_path + ".bad"); } catch { }
    }

    [Fact]
    public void Missing_file_yields_defaults()
    {
        var s = new SettingsStore(_path).Load();
        Assert.True(s.RestartExplorerOnApply);
        Assert.Null(s.DefaultTerminal);   // null = "not chosen" → effective default resolved at use-time
    }

    [Fact]
    public void Round_trips_all_settings()
    {
        var store = new SettingsStore(_path);
        store.Save(new AppSettings { RestartExplorerOnApply = false, DefaultTerminal = "pwsh" });

        var loaded = store.Load();
        Assert.False(loaded.RestartExplorerOnApply);
        Assert.Equal("pwsh", loaded.DefaultTerminal);
    }

    [Fact]
    public void Explicit_command_prompt_is_distinct_from_unset()
    {
        var store = new SettingsStore(_path);
        store.Save(new AppSettings { DefaultTerminal = "" });   // user explicitly picked Command Prompt

        Assert.Equal("", store.Load().DefaultTerminal);          // "" survives, not coalesced to null
    }

    [Fact]
    public void Save_is_atomic_leaving_no_tmp_file_and_overwriting_in_place()
    {
        // Regression: Save used a direct WriteAllText, so a crash mid-write left
        // settings.json truncated and Load silently reverted to defaults. Now it
        // uses the same .tmp + rename pattern as the other stores.
        var store = new SettingsStore(_path);
        store.Save(new AppSettings { RestartExplorerOnApply = false });
        store.Save(new AppSettings { RestartExplorerOnApply = false, DefaultTerminal = "pwsh" });

        Assert.False(File.Exists(_path + ".tmp"), "tmp file should have been renamed away");
        Assert.Equal("pwsh", store.Load().DefaultTerminal);
    }

    [Fact]
    public void Corrupt_file_yields_defaults_and_is_backed_up()
    {
        File.WriteAllText(_path, "{not valid json");
        var store = new SettingsStore(_path);

        var s = store.Load();

        Assert.True(s.RestartExplorerOnApply);   // defaults
        Assert.True(File.Exists(_path + ".bad"), "unreadable file should be parked at .bad");
        Assert.Equal("{not valid json", File.ReadAllText(_path + ".bad"));
    }
}
