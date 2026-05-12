using System.Collections.Generic;
using RCMM.Core.Services;

namespace RCMM.Tests;

public sealed class FakeMuiStringResolver : IMuiStringResolver
{
    public Dictionary<string, string?> Map { get; } = new();

    public string? Resolve(string? mui)
    {
        if (string.IsNullOrEmpty(mui)) return null;
        if (mui[0] != '@') return mui;
        return Map.TryGetValue(mui, out var v) ? v : null;
    }
}
