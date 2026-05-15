using System.Collections.Generic;

namespace RCMM.Core.Models;

/// <summary>
/// Root document for additions.json. Schema versioned for future migrations.
///   v1 — flat folders (no <see cref="AdditionFolder.ParentFolderId"/>, no
///        <see cref="AdditionFolder.Scope"/>). Single level of nesting only.
///   v2 — folder nesting (max 3 levels in the editor), explicit folder Scope,
///        item order captured by list position in <see cref="Folders"/>/<see cref="Entries"/>.
/// AdditionStore.Load migrates v1 → v2 transparently.
/// </summary>
public sealed record AdditionState
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public IReadOnlyList<AdditionFolder> Folders { get; init; } = new List<AdditionFolder>();
    public IReadOnlyList<AdditionEntry> Entries { get; init; } = new List<AdditionEntry>();
}
