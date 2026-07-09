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

    // --- Template-update tracking (schema v3) ---
    /// <summary>If this entry was cloned from a built-in template, the template's
    /// stable id (its Name). Null for hand-authored entries. Lets RCMM notice when
    /// we later change that template and offer to update the entry.</summary>
    public string? SourceTemplateId { get; init; }
    /// <summary>Content hash of the template's tracked fields (Command, FileTypes,
    /// Scope, RunMode, WorkingDir) as they were when the entry was added or last
    /// updated. A mismatch against the live template means an update is available.</summary>
    public string? AppliedTemplateHash { get; init; }
    /// <summary>The template hash the user last chose to Skip. Suppresses the
    /// update prompt until we change the template again (hash moves on).</summary>
    public string? SkippedTemplateHash { get; init; }

    // --- Hidden state (schema v4) ---
    /// <summary>True when the user has hidden this entry from the Show/Hide page.
    /// Hidden-ness must be persisted here rather than left as a LegacyDisable value
    /// in the registry: Apply purges every RCMM.-prefixed key and rewrites it from
    /// this store, so a registry-only marker would be destroyed on the next Apply.
    /// <see cref="Services.AdditionApplier.WriteEntry"/> re-emits LegacyDisable.</summary>
    public bool Hidden { get; init; }
}
