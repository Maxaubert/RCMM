using System.Collections.Generic;

namespace RCMM.Core.Models;

/// <summary>Root document for additions.json. Schema versioned for future migrations.</summary>
public sealed record AdditionState
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<AdditionFolder> Folders { get; init; } = new List<AdditionFolder>();
    public IReadOnlyList<AdditionEntry> Entries { get; init; } = new List<AdditionEntry>();
}
