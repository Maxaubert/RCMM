namespace RCMM.Core.Models;

// One raw scanner discovery. Deliberately carries NO Source / IsBuiltIn /
// IsHidden: the scanners used to compute those with their own (drifted)
// heuristics, but the only production consumer — EntryScanner.ScanAsCaptures —
// dropped them, and the real classification happens in MainViewModel
// (ResolveSourceAndBuiltIn / AllTargetsHidden). Keeping the dead fields made a
// scanner-side "fix" look like working code while changing nothing in the app.
public sealed record ContextMenuEntry
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required Scope Scope { get; init; }
    public required EntryKind Kind { get; init; }
    public required string RegistryPath { get; init; }
    public required string OriginalKeyName { get; init; }
    public string? IconPath { get; init; }
    public string? CommandLine { get; init; }
    public string? Clsid { get; init; }
}
