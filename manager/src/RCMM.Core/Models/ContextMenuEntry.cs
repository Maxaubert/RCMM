namespace RCMM.Core.Models;

public sealed record ContextMenuEntry
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Source { get; init; }
    public required Scope Scope { get; init; }
    public required EntryKind Kind { get; init; }
    public required string RegistryPath { get; init; }
    public required string OriginalKeyName { get; init; }
    public string? IconPath { get; init; }
    public string? CommandLine { get; init; }
    public string? Clsid { get; init; }
    public bool IsBuiltIn { get; init; }
    public bool IsHidden { get; init; }
}
