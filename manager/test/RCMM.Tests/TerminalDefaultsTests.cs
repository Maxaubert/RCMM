using System.Collections.Generic;
using System.Linq;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Tests;

public class TerminalDefaultsTests
{
    private static AdditionEntry E(string id, RunMode mode, string command, string? terminal = null)
        => new AdditionEntry
        {
            Id = id, Name = id, Command = command, WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = mode, Terminal = terminal,
        };

    [Fact]
    public void Rewrites_only_visible_terminal_entries()
    {
        var entries = new List<AdditionEntry>
        {
            E("shell", RunMode.VisibleTerminal, "git pull"),                 // opens a terminal → rewritten
            E("gui",   RunMode.Background, "\"C:\\app\\Code.exe\" \"%V\""),  // GUI launch → untouched
        };

        var result = TerminalDefaults.ApplyToExisting(entries, "pwsh");

        Assert.Equal("pwsh", result.Single(e => e.Id == "shell").Terminal);
        Assert.Null(result.Single(e => e.Id == "gui").Terminal);
    }

    [Fact]
    public void Empty_terminal_normalizes_to_null()
    {
        var entries = new List<AdditionEntry> { E("shell", RunMode.VisibleTerminal, "git pull", "wt") };

        var result = TerminalDefaults.ApplyToExisting(entries, "");   // "Command Prompt" default

        Assert.Null(result.Single().Terminal);
    }

    [Fact]
    public void Unchanged_entries_keep_the_same_instance()
    {
        // A visible-terminal entry already on the target terminal, and a GUI entry,
        // should both come back as the very same object (no needless churn / pending dirty).
        var shell = E("shell", RunMode.VisibleTerminal, "git pull", "pwsh");
        var gui = E("gui", RunMode.Background, "\"C:\\app\\Code.exe\" \"%V\"");
        var entries = new List<AdditionEntry> { shell, gui };

        var result = TerminalDefaults.ApplyToExisting(entries, "pwsh");

        Assert.Same(shell, result.Single(e => e.Id == "shell"));
        Assert.Same(gui, result.Single(e => e.Id == "gui"));
    }
}
