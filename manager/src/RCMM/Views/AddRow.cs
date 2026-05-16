namespace RCMM.Views;

/// <summary>
/// Per-row data adapter for the Add page's left + middle lists. Pure data —
/// every visual is painted from code-behind (<c>ApplyLeftRowVisuals</c> /
/// <c>ApplyMidRowVisuals</c>) wired off the row's <c>Loaded</c> +
/// <c>DataContextChanged</c> events. We tried XAML <c>{Binding}</c> + INPC
/// first; container recycling under WinUI 3 ListView virtualization kept
/// stale subscriptions alive on recycled containers, which manifested as
/// the selection bar painting on the wrong rows. Driving visuals from code
/// against the current DataContext sidesteps that entirely.
/// </summary>
public sealed class AddRow
{
    public required string Kind { get; init; }     // "folder" | "entry"
    public required string Id   { get; init; }
    public required string Bucket { get; init; }   // "root" | parent folder id
    public int Indent { get; init; }
    public required string Label { get; init; }
    public string? Badge { get; init; }
    public string? IconValue { get; init; }
    public bool IsExpanded { get; init; }
    public string? SubText { get; init; }           // middle pane only
    public string? Ordinal { get; init; }           // middle pane only

    public string TwistGlyph => Kind == "folder" ? (IsExpanded ? "▾" : "▸") : "";
}
