using System;
using System.Collections.Generic;

namespace RCMM.Core.Models;

public sealed record CapturedItem
{
    public required string TargetPath { get; init; }
    public required int Position { get; init; }
    public required string DisplayName { get; init; }
    public string? Verb { get; init; }
    public string? OwnerClsid { get; init; }
    public byte[]? IconBytes { get; init; }
    // Hint for icon resolution. Registry-scanned shellex rows already know the
    // handler DLL path (from CLSID\<clsid>\InprocServer32) — passing it through
    // lets MainViewModel.ResolveIconPath fall back to it when no CommandStore
    // Icon / verb-level Icon / IExplorerCommand::GetIcon source produces one.
    public string? IconHint { get; init; }
    // True when DisplayName is text the shell actually renders in a menu — a
    // live IContextMenu capture, an IExplorerCommand::GetTitle result, or a
    // probe-emitted menu string. Such names must never be discarded by the
    // technical-name heuristic: a single-token mixed-case brand name
    // ("TeraCopy...", "PowerRename") looks like a class identifier but IS the
    // real menu entry. False for registry-derived fallback names
    // (FileDescriptions, CLSIDs, raw key names), which the heuristic may judge.
    public bool DisplayNameIsMenuText { get; init; }
    public bool IsSeparator { get; init; }
    public bool IsSubmenu { get; init; }
    public IReadOnlyList<CapturedItem> Children { get; init; } = Array.Empty<CapturedItem>();
}
