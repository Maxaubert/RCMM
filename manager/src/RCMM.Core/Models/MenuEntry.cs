using System.Collections.Generic;

namespace RCMM.Core.Models;

public sealed record MenuEntry
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Source { get; init; }
    public byte[]? IconBytes { get; init; }
    public string? IconPath { get; init; }
    public required IReadOnlyList<HideTarget> HideTargets { get; init; }
    public bool IsBuiltIn { get; init; }
    public bool IsHidden { get; init; }
    public bool IsSubmenu { get; init; }
    // DisplayName provenance (see CapturedItem.DisplayNameIsMenuText): true when
    // the name is real rendered menu text, which exempts the row from the
    // technical-name filter regardless of how identifier-like it looks.
    public bool DisplayNameIsMenuText { get; init; }

    public bool CanHide => HideTargets.Count > 0;
}
