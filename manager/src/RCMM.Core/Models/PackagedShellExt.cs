namespace RCMM.Core.Models;

/// <summary>
/// A modern packaged shell extension discovered under
/// HKLM\SOFTWARE\Classes\PackagedCom\Package\&lt;pkg&gt;\Server\&lt;idx&gt;.
/// The Clsid is the SurrogateAppId — that's the GUID Explorer activates
/// for context menu purposes and the one we put into the Blocked list.
/// </summary>
public sealed record PackagedShellExt
{
    public required string Clsid { get; init; }
    public required string PackageFullName { get; init; }
    public required string DisplayName { get; init; }
    public required string PublisherDisplayName { get; init; }
}
