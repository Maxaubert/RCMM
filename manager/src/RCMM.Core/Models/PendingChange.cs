namespace RCMM.Core.Models;

public enum PendingAction { Hide, Unhide }

public sealed record PendingChange(string EntryId, PendingAction Action, bool RequiresExplorerRestart);
