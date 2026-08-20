using System;
using System.Collections.Generic;
using RCMM.Core.Services;

namespace RCMM.Tests;

public sealed class FakeShellexInvoker : IShellexInvoker
{
    public Dictionary<string, List<string>> EmittedByClsid { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> TitleByClsid { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> IconByClsid { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> RegisteredClsids { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void RegisterExtraClsid(string clsid)
    {
        if (!string.IsNullOrWhiteSpace(clsid)) RegisteredClsids.Add(clsid);
    }

    public string? LookupIconPath(string? clsid)
        => clsid != null && IconByClsid.TryGetValue(clsid, out var icon) ? icon : null;

    public string? LookupTitle(string? clsid)
        => clsid != null && TitleByClsid.TryGetValue(clsid, out var title) ? title : null;

    public IEnumerable<string> LookupEmittedNames(string? clsid)
        => clsid != null && EmittedByClsid.TryGetValue(clsid, out var names)
            ? names
            : (IEnumerable<string>)Array.Empty<string>();

    public IReadOnlyDictionary<string, string> BuildDisplayNameToClsidMap()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (clsid, names) in EmittedByClsid)
            foreach (var n in names)
                if (!result.ContainsKey(n)) result[n] = clsid;
        return result;
    }
}
