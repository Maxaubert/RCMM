using System;
using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

/// <summary>
/// The per-entry "Terminal" choice — which terminal a visible-terminal entry
/// opens in. <see cref="OptionsFor"/> lists what's installed for the editor;
/// <see cref="Wrap"/> turns a stored choice + command into the launch string
/// written to the registry.
///
/// Two cases, because they're mechanically different:
///   • A plain command (<see cref="RunMode.VisibleTerminal"/>) is RUN in the
///     chosen shell — cmd / PowerShell / pwsh / WSL — so the command is wrapped
///     in that shell's "run and stay open" form.
///   • A script action (a Background entry that opens a terminal, e.g. the .ps1
///     smart actions) keeps its own interpreter and is only HOSTED in the chosen
///     terminal (prefix Windows Terminal), so we never re-quote a script
///     invocation into a different shell and break it.
///
/// Git Bash is intentionally not a built-in option: launching a command through
/// mintty's git-bash.exe and keeping the window open is unreliable. Point a
/// Custom path at any terminal instead (best-effort "exe + command").
/// </summary>
public static class TerminalCatalog
{
    /// <summary>Sentinel option value meaning "the user typed a custom exe path".</summary>
    public const string Custom = "__custom__";

    /// <summary>One choice in the editor's Terminal dropdown. <see cref="Value"/>
    /// is what gets stored on the entry: "" (default), a known key, or
    /// <see cref="Custom"/> (the textbox path is stored instead).</summary>
    public sealed record Option(string Display, string Value);

    private static readonly IReadOnlyList<string> _pwshPaths = new[]
    {
        @"%ProgramFiles%\PowerShell\7\pwsh.exe",
        @"%ProgramFiles%\PowerShell\7-preview\pwsh.exe",
        @"%LOCALAPPDATA%\Microsoft\PowerShell\7\pwsh.exe",
    };

    /// <summary>
    /// Terminal options for the editor, filtered to what resolves on this PC.
    /// <paramref name="resolve"/> is the binary resolver (name + fallbacks → path
    /// or null) — injected so this stays unit-testable.
    /// </summary>
    public static IReadOnlyList<Option> OptionsFor(
        RunMode mode, Func<string, IReadOnlyList<string>?, string?> resolve)
    {
        var list = new List<Option>();
        bool hasWt = resolve("wt.exe", null) != null;

        if (mode == RunMode.VisibleTerminal)
        {
            list.Add(new Option("Command Prompt", ""));                 // default for plain commands
            if (hasWt) list.Add(new Option("Windows Terminal", "wt"));
            list.Add(new Option("Windows PowerShell", "powershell"));
            if (resolve("pwsh.exe", _pwshPaths) != null) list.Add(new Option("PowerShell 7", "pwsh"));
            if (resolve("wsl.exe", null) != null) list.Add(new Option("WSL", "wsl"));
        }
        else
        {
            list.Add(new Option("Default console", ""));                 // host-only choices
            if (hasWt) list.Add(new Option("Windows Terminal", "wt"));
        }
        list.Add(new Option("Custom…", Custom));
        return list;
    }

    /// <summary>
    /// The terminal a new entry should default to when the user hasn't chosen one:
    /// Windows Terminal if it's installed, otherwise Command Prompt (""). <paramref
    /// name="resolve"/> is the binary resolver (injected for testability).
    /// </summary>
    public static string DefaultPreferred(Func<string, IReadOnlyList<string>?, string?> resolve)
        => resolve("wt.exe", null) != null ? "wt" : "";

    /// <summary>True when the entry pops a visible terminal window, so the
    /// Terminal selector is relevant. Background entries qualify only if their
    /// command launches a shell/terminal and isn't run hidden.</summary>
    public static bool OpensVisibleTerminal(RunMode mode, string? command)
    {
        if (mode == RunMode.VisibleTerminal) return true;
        if (string.IsNullOrWhiteSpace(command)) return false;
        if (command!.IndexOf("-WindowStyle Hidden", StringComparison.OrdinalIgnoreCase) >= 0) return false;

        // First token = the launched program, whether bare ("powershell …") or a
        // quoted full path (""C:\…\powershell.exe" …" after %bin% resolution).
        var c = command.TrimStart();
        string first;
        if (c.StartsWith("\""))
        {
            int q = c.IndexOf('"', 1);
            first = q > 1 ? c.Substring(1, q - 1) : c;
        }
        else
        {
            int sp = c.IndexOf(' ');
            first = sp > 0 ? c.Substring(0, sp) : c;
        }
        var name = System.IO.Path.GetFileName(first).ToLowerInvariant();
        if (name.EndsWith(".exe")) name = name.Substring(0, name.Length - 4);
        return name is "powershell" or "pwsh" or "cmd" or "wt" or "wsl" or "bash" or "git-bash" or "conhost";
    }

    /// <summary>Build the registry launch string for a command, run mode, and
    /// terminal choice (null/empty = default behaviour).</summary>
    public static string Wrap(string command, RunMode mode, string? terminal)
    {
        var t = string.IsNullOrWhiteSpace(terminal) ? null : terminal!.Trim();
        bool isPath = t != null &&
                      (t.Contains('\\') || t.Contains('/') || t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        if (mode == RunMode.VisibleTerminal)
        {
            if (t == null || t == "cmd") return "cmd /k " + command;
            switch (t)
            {
                // "-d ." starts Windows Terminal in the verb's working dir (the
                // process cwd Explorer hands us) instead of wt's default profile dir.
                case "wt":         return "wt.exe -d . cmd /k " + command;
                case "powershell": return "powershell -NoExit -Command \"" + PsEscape(command) + "\"";
                case "pwsh":       return "pwsh -NoExit -Command \"" + PsEscape(command) + "\"";
                case "wsl":        return "wsl.exe -e bash -lic \"" + BashEscape(command) + "; exec bash\"";
            }
            if (isPath) return "\"" + t + "\" " + command;   // custom terminal — best effort
            return "cmd /k " + command;
        }

        // Background: host the existing command as-is (no shell re-interpretation).
        if (t == null || t == "cmd" || t == "powershell") return command;
        if (t == "wt") return "wt.exe -d . " + command;
        if (isPath) return "\"" + t + "\" " + command;
        return command;
    }

    // -Command "..." — double embedded quotes (covers the common no-quote case
    // cleanly; deeply-quoted commands are better served by a Custom terminal).
    private static string PsEscape(string s) => s.Replace("\"", "\"\"");
    private static string BashEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
