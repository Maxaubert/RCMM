using System.Collections.Generic;
using System.Linq;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Tests;

public class TerminalCatalogTests
{
    // ---- Wrap: plain command (VisibleTerminal) runs in the chosen shell ----

    [Fact]
    public void Visible_default_is_cmd_k()
    {
        Assert.Equal("cmd /k git pull", TerminalCatalog.Wrap("git pull", RunMode.VisibleTerminal, null));
        Assert.Equal("cmd /k git pull", TerminalCatalog.Wrap("git pull", RunMode.VisibleTerminal, ""));
        Assert.Equal("cmd /k git pull", TerminalCatalog.Wrap("git pull", RunMode.VisibleTerminal, "cmd"));
    }

    [Fact]
    public void Visible_windows_terminal_hosts_cmd_in_working_dir()
    {
        Assert.Equal("wt.exe -d . cmd /k git pull",
            TerminalCatalog.Wrap("git pull", RunMode.VisibleTerminal, "wt"));
    }

    [Fact]
    public void Visible_powershell_runs_command()
    {
        Assert.Equal("powershell -NoExit -Command \"git pull\"",
            TerminalCatalog.Wrap("git pull", RunMode.VisibleTerminal, "powershell"));
        Assert.Equal("pwsh -NoExit -Command \"git pull\"",
            TerminalCatalog.Wrap("git pull", RunMode.VisibleTerminal, "pwsh"));
    }

    [Fact]
    public void Visible_custom_path_prefixes_the_command()
    {
        Assert.Equal("\"C:\\tools\\alacritty.exe\" git pull",
            TerminalCatalog.Wrap("git pull", RunMode.VisibleTerminal, @"C:\tools\alacritty.exe"));
    }

    // ---- Wrap: script action (Background) is only hosted, never re-shelled ----

    [Fact]
    public void Background_default_is_unchanged()
    {
        var cmd = "powershell -NoProfile -File \"x.ps1\" \"%1\"";
        Assert.Equal(cmd, TerminalCatalog.Wrap(cmd, RunMode.Background, null));
        Assert.Equal(cmd, TerminalCatalog.Wrap(cmd, RunMode.Background, "powershell"));
    }

    [Fact]
    public void Background_windows_terminal_prefixes_without_requoting()
    {
        var cmd = "powershell -NoProfile -File \"x.ps1\" \"%1\"";
        Assert.Equal("wt.exe -d . " + cmd, TerminalCatalog.Wrap(cmd, RunMode.Background, "wt"));
    }

    // ---- OpensVisibleTerminal ----

    [Theory]
    [InlineData(RunMode.VisibleTerminal, "git pull", true)]
    [InlineData(RunMode.Background, "powershell -NoProfile -File x.ps1", true)]
    [InlineData(RunMode.Background, "wt.exe -d \"%V\"", true)]
    [InlineData(RunMode.Background, "\"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoExit", true)]
    [InlineData(RunMode.Background, "powershell -WindowStyle Hidden -File x.ps1", false)]
    [InlineData(RunMode.Background, "\"C:\\app\\Code.exe\" \"%V\"", false)]
    public void OpensVisibleTerminal_classifies(RunMode mode, string command, bool expected)
    {
        Assert.Equal(expected, TerminalCatalog.OpensVisibleTerminal(mode, command));
    }

    // ---- OptionsFor: filtered by what resolves; always offers a default + custom ----

    [Fact]
    public void Options_visible_includes_default_and_custom_and_installed()
    {
        // Resolver that "finds" wt + pwsh but not wsl.
        string? Resolve(string name, IReadOnlyList<string>? _) =>
            name is "wt.exe" or "pwsh.exe" ? @"C:\fake\" + name : null;

        var opts = TerminalCatalog.OptionsFor(RunMode.VisibleTerminal, Resolve);
        var values = opts.Select(o => o.Value).ToList();

        Assert.Equal("", values.First());                       // default first
        Assert.Equal(TerminalCatalog.Custom, values.Last());    // custom last
        Assert.Contains("powershell", values);                  // always present
        Assert.Contains("wt", values);                          // resolved
        Assert.Contains("pwsh", values);                        // resolved
        Assert.DoesNotContain("wsl", values);                   // not resolved
    }

    // ---- DefaultPreferred: Windows Terminal when installed, else Command Prompt ----

    [Fact]
    public void DefaultPreferred_is_wt_when_installed()
    {
        string? Resolve(string name, IReadOnlyList<string>? _) => name == "wt.exe" ? @"C:\wt.exe" : null;
        Assert.Equal("wt", TerminalCatalog.DefaultPreferred(Resolve));
    }

    [Fact]
    public void DefaultPreferred_is_command_prompt_when_wt_absent()
    {
        string? Resolve(string name, IReadOnlyList<string>? _) => null;   // nothing installed
        Assert.Equal("", TerminalCatalog.DefaultPreferred(Resolve));
    }

    [Fact]
    public void Options_background_is_host_only()
    {
        string? Resolve(string name, IReadOnlyList<string>? _) => name == "wt.exe" ? @"C:\wt.exe" : null;
        var values = TerminalCatalog.OptionsFor(RunMode.Background, Resolve).Select(o => o.Value).ToList();
        Assert.Equal(new[] { "", "wt", TerminalCatalog.Custom }, values);  // no shells
    }
}
