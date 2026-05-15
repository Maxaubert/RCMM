namespace RCMM.Core.Models;

/// <summary>
/// A user-defined grouping of entries. Renders as a classic shell submenu
/// via HKCU\Software\Classes\&lt;scope&gt;\shell\RCMM.{Id}\ExtendedSubCommandsKey.
/// One level deep — folders cannot contain other folders in v1.
/// </summary>
public sealed record AdditionFolder
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Icon { get; init; }
}
