namespace RCMM.Core.Services;

public interface IMuiStringResolver
{
    /// <summary>
    /// Resolves a Windows MUI indirect-string reference like
    /// "@C:\Windows\System32\shell32.dll,-8506" to its localized display text.
    /// Returns the input unchanged when it isn't an @-reference.
    /// Returns null when the reference cannot be resolved.
    /// </summary>
    string? Resolve(string? mui);
}
