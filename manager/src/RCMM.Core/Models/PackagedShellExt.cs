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
    /// <summary>
    /// Absolute path to a PNG asset declared as the package's logo (resolved from
    /// AppxManifest.xml — Square44x44Logo, falling back to Properties/Logo).
    /// Many packaged shellex DLLs carry zero icon resources because the publisher
    /// expects Windows to render the package's logo asset; this gives us the same
    /// fallback even when the WindowsApps folder isn't otherwise enumerable.
    /// </summary>
    public string? LogoPath { get; init; }
}
