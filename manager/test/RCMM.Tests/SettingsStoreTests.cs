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

    public void Dispose() { try { File.Delete(_path); } catch { } }

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
}
