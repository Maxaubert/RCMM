using System;

namespace RCMM.Core.ViewModels;

public enum OriginFilter { All, Apps, Windows }
public enum VisibilityFilter { All, Visible, Hidden }

/// <summary>
/// Pure predicate behind the unified Show/Hide list's chip row + search box.
/// Lives in Core rather than the page so the chip/search composition is
/// unit-testable without WinUI.
/// </summary>
public static class EntryListFilter
{
    public static bool Matches(EntryRowViewModel row, OriginFilter origin,
                               VisibilityFilter visibility, string? search)
    {
        if (origin == OriginFilter.Apps && row.IsBuiltIn) return false;
        if (origin == OriginFilter.Windows && !row.IsBuiltIn) return false;
        if (visibility == VisibilityFilter.Visible && row.IsHidden) return false;
        if (visibility == VisibilityFilter.Hidden && !row.IsHidden) return false;
        var needle = search?.Trim();
        if (!string.IsNullOrEmpty(needle)
            && !row.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }
}
