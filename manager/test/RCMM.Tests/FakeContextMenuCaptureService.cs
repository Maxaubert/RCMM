using System.Collections.Generic;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Tests;

public sealed class FakeContextMenuCaptureService : IContextMenuCaptureService
{
    public Dictionary<string, List<CapturedItem>> Map { get; } = new();

    public IReadOnlyList<CapturedItem> CaptureAll(IReadOnlyList<string> targetPaths)
    {
        var result = new List<CapturedItem>();
        foreach (var path in targetPaths)
        {
            if (Map.TryGetValue(path, out var items))
                result.AddRange(items);
        }
        return result;
    }
}
