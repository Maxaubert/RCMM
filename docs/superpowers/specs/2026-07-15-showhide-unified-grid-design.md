# Unified Show/Hide grid + title bar gear alignment

**Date:** 2026-07-15
**Status:** Approved

## Goal

Merge the Show/Hide section's App/Windows two-card split into a single
unified entry list with a filter bar, rendered as a responsive multi-column
grid instead of one row per line. Also fix the settings gear so it aligns
with the system caption buttons.

## Decisions made during brainstorming

- **Replace the landing cards.** Navigation goes straight to the unified
  list; `ShowHidePage` (the two-card page) is deleted. The old page's
  App/Windows counts survive as chip labels.
- **Filters: App/Windows + visibility only.** Both driven by flags entries
  already carry (`IsBuiltIn`, `IsHidden`). No filter by `Source`, no
  Item/Submenu filter, and **no hand-curated app-category taxonomy** (was
  considered; rejected as unmaintainable — entries carry no such data).
- **Grid layout** over denser rows, chosen from the layout options.
- **Settings gear**: align with the caption buttons, top-right — it is
  already in the title bar but centers in the 48px bar while the system
  caption buttons are shorter, so it sits visibly lower (user screenshot).

## Design

### 1. Unified page (evolves `ScopePage`)

- `ScopePage` becomes the single Show/Hide destination. `MainWindow` /
  landing-page navigation that previously targeted `ShowHidePage` targets it
  directly; `ShowHidePage.xaml(.cs)` is deleted and `ListFilter` navigation
  plumbing (`NavArgs.Filter`) goes with it — the filter is now page-local UI
  state, not a navigation argument.
- The landing page's "Show / hide" card keeps its copy and donut but
  navigates to the unified page.

### 2. Filter bar

- Two chip groups above the list plus the existing search box, all composing
  (AND semantics):
  - **All | Apps (n) | Windows (n)** — `IsBuiltIn`; counts computed from the
    live entry list, updating after Rescan.
  - **All | Visible | Hidden** — `IsHidden`.
- Defaults: All / All. Chips restyle in the existing dark flat language
  (match the Templates page's chip row).
- Filter predicate lives in a testable Core viewmodel (see Testing).

### 3. Grid layout

*(Amended after mockup review: the owner picked the "square, tile is the
toggle" variant over the original two-line cards.)*

- Responsive grid of **square tiles**, ~132px minimum, sharing the row width
  exactly (`GridView` + `ItemsWrapGrid`, `ItemWidth`/`ItemHeight` computed
  from the viewport on `Loaded` + `SizeChanged`); virtualization required
  (60+ entries with icons).
- **The whole tile is the on/off control** — no `ToggleSwitch`. Tile face:
  32px icon + display name (2-line wrap, centered). Visible entries sit
  bright with a lime border, lime-glow fill, and a lime corner dot (the dot
  keeps state readable without relying on hue alone); hidden entries dim to
  half opacity and brighten on hover so their label stays readable.
- Publisher + kind move to the tile tooltip (`Source · Item/Submenu`);
  the BUILT-IN badge leaves the tile face (the Apps/Windows chips carry
  that dimension).
- Toggling goes through `GridView.ItemClick`, which also fires for
  Enter/Space on a focused tile, keeping hide/unhide keyboard-operable.
- Non-hideable entries show a small "locked" label and don't respond to
  clicks (`CanHide == false`).

### 4. Settings gear alignment

- The gear button in `MainWindow.xaml` stays top-right, immediately left of
  the system caption buttons, but its vertical center matches theirs
  (today: centered in the 48px `AppTitleBar` row vs the shorter caption
  strip). Fix via alignment/height on the button (or title bar height
  metrics), not by moving it out of the title bar.

### 5. Out of scope

- No changes to discovery, rename, dedupe, or the step-9 visible-list
  filter (`MainViewModel.AllEntries`) — this is presentation only.
- No Source-based filtering or curated categories.
- Landing page donut and the rest of the landing layout unchanged.

## Testing

- Core: unit tests for the filter predicate (App/Windows × Visible/Hidden ×
  search needle compositions) on a small fixture list.
- UI: build + screenshot verification (grid at narrow/wide widths, chips,
  gear alignment against caption buttons).

## Process

Separate from PR #25: new GitHub issue, new branch + worktree off `main`,
spec → plan → subagent execution.
