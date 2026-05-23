using System.Collections.Generic;

namespace RCMM.Core.Models;

/// <summary>
/// One user-added or template-cloned right-click menu entry. Stored in
/// %APPDATA%\RCMM\additions.json and written to the Windows registry on Apply.
/// </summary>
public sealed record AdditionEntry
{
    /// <summary>Globally unique id — used to derive the registry verb name "RCMM.{Id}".</summary>
    public required string Id { get; init; }
    /// <summary>Display text the user sees in the right-click menu.</summary>
    public required string Name { get; init; }
    /// <summary>Icon spec (file path or "path,index"). Null = no Icon value written; Windows derives one from the command's exe.</summary>
    public string? Icon { get; init; }
    /// <summary>Bare command — RunMode controls how it's wrapped at registry-write time.</summary>
    public required string Command { get; init; }
    /// <summary>Working directory shell var, typically "%V" for "the right-clicked folder".</summary>
    public required string WorkingDir { get; init; }
    public required AdditionScope Scope { get; init; }
    /// <summary>Only relevant when Scope == File. Each extension gets its own registry registration.</summary>
    public IReadOnlyList<string>? FileTypes { get; init; }
    /// <summary>Null = top-level entry; else points to AdditionFolder.Id.</summary>
    public string? FolderId { get; init; }
    public required RunMode RunMode { get; init; }
    /// <summary>Which terminal a visible-terminal entry opens in. Null/empty =
    /// default (cmd for plain commands; the command's own host otherwise).
    /// Known keys: <c>wt</c> / <c>powershell</c> / <c>pwsh</c> / <c>wsl</c>, or a
    /// custom terminal exe path. Applied at write-time by
    /// <see cref="Services.TerminalCatalog.Wrap"/>.</summary>
    public string? Terminal { get; init; }
}
