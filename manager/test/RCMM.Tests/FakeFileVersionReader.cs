using System.Collections.Generic;
using RCMM.Core.Services;

namespace RCMM.Tests;

public sealed class FakeFileVersionReader : IFileVersionReader
{
    public Dictionary<string, FileVersion> Map { get; } = new();

    public FileVersion Read(string path)
        => Map.TryGetValue(path, out var v) ? v : new FileVersion(null, null, null);
}
