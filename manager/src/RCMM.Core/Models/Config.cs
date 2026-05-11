using System.Collections.Generic;

namespace RCMM.Core.Models;

public sealed class Config
{
    public int SchemaVersion { get; set; } = 1;
    public List<ContextMenuEntry> KnownEntries { get; set; } = new();
}
