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
    /// <summary>
    /// Absolute path to the package's COM-registered DLL (or null if it couldn't be
    /// resolved). Used as the icon source: ExtractIconEx returns the package's icon
    /// resources, which is the closest we can get without parsing AppxManifest.xml.
    /// </summary>
    public string? DllPath { get; init; }
}
