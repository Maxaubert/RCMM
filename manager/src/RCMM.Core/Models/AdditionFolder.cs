namespace RCMM.Core.Models;

/// <summary>
/// A user-defined grouping of entries. Renders as a classic shell submenu
/// via HKCU\Software\Classes\&lt;scope&gt;\shell\RCMM.{Id}\ExtendedSubCommandsKey.
/// May nest under another folder via <see cref="ParentFolderId"/>; the visible
/// nesting depth is capped at 3 in the editor (root → A → B → C → entries).
/// </summary>
public sealed record AdditionFolder
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Icon { get; init; }
    /// <summary>
    /// Id of the parent folder this one nests under. Null = top-level.
    /// Added in schema v2.
    /// </summary>
    public string? ParentFolderId { get; init; }
    /// <summary>
    /// Folder's own scope. The folder verb's actual placement still follows
    /// the union of its descendants' scopes (so a folder full of File entries
    /// still appears on files), but this drives the default scope for new
    /// child entries created via the editor. Added in schema v2.
    /// </summary>
    public AdditionScope Scope { get; init; } = AdditionScope.FolderBackground;
}
