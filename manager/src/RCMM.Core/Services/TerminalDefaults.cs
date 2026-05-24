using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

/// <summary>
/// Applies a chosen default terminal to a set of existing additions. Only entries
/// that actually open a visible terminal are touched — a GUI-launch entry has no
/// meaningful terminal, so rewriting it would be noise (or, for a Background entry,
/// could wrap it in an unwanted host). Pure: the caller persists + re-applies.
/// </summary>
public static class TerminalDefaults
{
    /// <summary>
    /// Return <paramref name="entries"/> with <see cref="AdditionEntry.Terminal"/>
    /// set to <paramref name="terminal"/> on every entry that opens a visible
    /// terminal; all others are returned unchanged (same instance). An empty/whitespace
    /// terminal normalizes to null (Command Prompt), matching how the editor stores
    /// the default choice.
    /// </summary>
    public static IReadOnlyList<AdditionEntry> ApplyToExisting(
        IReadOnlyList<AdditionEntry> entries, string? terminal)
    {
        var norm = string.IsNullOrWhiteSpace(terminal) ? null : terminal!.Trim();
        var result = new List<AdditionEntry>(entries.Count);
        foreach (var e in entries)
        {
            if (TerminalCatalog.OpensVisibleTerminal(e.RunMode, e.Command) &&
                !TerminalEquals(e.Terminal, norm))
            {
                result.Add(e with { Terminal = norm });
            }
            else
            {
                result.Add(e);
            }
        }
        return result;
    }

    /// <summary>Treats null and "" as the same terminal (both = Command Prompt).</summary>
    private static bool TerminalEquals(string? a, string? b)
        => (string.IsNullOrEmpty(a) ? null : a) == (string.IsNullOrEmpty(b) ? null : b);
}
