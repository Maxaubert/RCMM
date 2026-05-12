using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public interface IContextMenuCaptureService
{
    /// <summary>
    /// Captures the context menu for each provided target path and returns
    /// one CapturedItem per (target × menu item) pair. No deduplication —
    /// callers (MainViewModel) handle merging.
    /// </summary>
    IReadOnlyList<CapturedItem> CaptureAll(IReadOnlyList<string> targetPaths);
}
