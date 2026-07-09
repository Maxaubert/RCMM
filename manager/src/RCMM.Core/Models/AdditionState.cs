using System.Collections.Generic;

namespace RCMM.Core.Models;

/// <summary>
/// Root document for additions.json. Schema versioned for future migrations.
///   v1 — flat folders (no <see cref="AdditionFolder.ParentFolderId"/>, no
///        <see cref="AdditionFolder.Scope"/>). Single level of nesting only.
///   v2 — folder nesting (max 3 levels in the editor), explicit folder Scope,
///        item order captured by list position in <see cref="Folders"/>/<see cref="Entries"/>.
///   v3 — template-update tracking: back-fills <see cref="AdditionEntry.SourceTemplateId"/>
///        and <see cref="AdditionEntry.AppliedTemplateHash"/> on entries whose Name
///        matches a built-in template, so RCMM can offer updates when we change one.
///   v4 — <see cref="AdditionEntry.Hidden"/>. Absent in older documents, which
///        deserializes to false — every pre-v4 entry was visible by construction,
///        so the migration is a no-op beyond the version stamp.
/// AdditionStore.Load migrates older schemas transparently.
/// </summary>
public sealed record AdditionState
{
    public const int CurrentSchemaVersion = 4;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public IReadOnlyList<AdditionFolder> Folders { get; init; } = new List<AdditionFolder>();
    public IReadOnlyList<AdditionEntry> Entries { get; init; } = new List<AdditionEntry>();
}
