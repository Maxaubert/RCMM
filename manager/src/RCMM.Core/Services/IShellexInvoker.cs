using System.Collections.Generic;

namespace RCMM.Core.Services;

/// <summary>
/// Seam over <see cref="ShellexInvoker"/> so the rescan pipeline's rename /
/// filter decisions (which depend on what handlers emitted during the COM
/// probe) can be exercised in tests without touching real COM or the real
/// registry. Production always wires the concrete <see cref="ShellexInvoker"/>.
/// </summary>
public interface IShellexInvoker
{
    void RegisterExtraClsid(string clsid);
    string? LookupIconPath(string? clsid);
    string? LookupTitle(string? clsid);
    IEnumerable<string> LookupEmittedNames(string? clsid);
    IReadOnlyDictionary<string, string> BuildDisplayNameToClsidMap();
}
