# Templates-only Add page

**Date:** 2026-07-15
**Status:** Approved

## Goal

Trim the "Add to menu" section down to a templates-only workflow. Users add
entries exclusively from the built-in template catalogue and then structure
them: position (drag order), folder placement (submenus), name, and icon.
The ad-hoc "create your own entry" path is removed entirely.

## Decisions made during brainstorming

- **Folders stay.** "+ New folder", 3-level nesting, and drag-reorder remain
  the structuring tools. Only the ad-hoc "+ New entry" path is removed.
- **Entry editor keeps Name + Folder + Icon** (plus Delete). Rename is cheap
  and harmless (e.g. shorten "Open project in VS Code" to "VS Code").
  Command, Working dir, Run mode, Terminal, Scope, and File types disappear
  from the editor; templates run exactly as shipped. No read-only display of
  the removed fields.
- **Hand-authored entries are dropped**, not grandfathered. A schema v5
  migration removes them on load; their registry keys vanish on the next
  Apply. Silent drop (one log line, no dialog).

## Design

### 1. Add page UI (`manager/src/RCMM/Views/AddPage.xaml` + `.xaml.cs`)

- Toolbar drops "+ New entry". Remaining actions: **Browse templates**
  (primary path) and **+ New folder**.
- Header copy changes to describe templates only, e.g.: "Add entries to the
  (classic) right-click menu from the template catalogue. Group them into
  folders that render as submenus. Drag to reorder, or drop onto a folder to
  move into it."
- Entry editor pane trims to: **Name**, **Folder** (parent submenu),
  **Icon** (existing picker: library icons, Choose/Clear, plus the advanced
  custom-path box), and **Delete**. The Command, Working dir, Run mode,
  Terminal, Scope, and File types rows are removed from XAML and code-behind.
- Folder editor stays as-is (Name, Parent folder, Icon, Delete).
- Left/middle panes, drag-reorder, 3-level nesting cap, and the Browse
  Templates page are unchanged.
- Terminal choice for visible-terminal templates is governed solely by the
  existing Settings "default terminal" preference (entries keep
  `Terminal = null` and fall back to it).

### 2. Data and migration (`AdditionState`, `AdditionStore`)

- `AdditionState.CurrentSchemaVersion`: 4 → 5.
- New v5 step in `AdditionStore.MigrateIfNeeded`: drop every entry with
  `SourceTemplateId == null` (hand-authored). Folders are never dropped.
  Log the dropped count under the `addstore` category.
- No new registry code: Apply already purges all `RCMM.*` keys and rewrites
  from the store, so dropped entries clean themselves up on the next Apply.
- Template-derived entries keep everything: hidden state, folder placement,
  order, icon, and template-update tracking
  (`SourceTemplateId` / `AppliedTemplateHash` / `SkippedTemplateHash`).
- The v3 name-matching migration stays — old files still upgrade v1 → v5 in
  one pass, and v3 decides which pre-v3 entries count as template-derived
  (and therefore survive v5).

### 3. Code removal

- `NewEntry_Click` and the blank-entry creation path are deleted.
- All `AddPage.xaml.cs` editor plumbing for the removed fields is deleted
  (Command/WorkingDir text boxes, RunMode/Terminal/Scope combo handling,
  FileTypes parsing).
- `AdditionEntry` keeps its Command / WorkingDir / Scope / RunMode /
  Terminal / FileTypes fields: `AdditionApplier` and the template clone path
  still need them; they're just no longer user-editable.
- `AddPageViewModel` API (`AddEntry`, `ReplaceEntry`, moves, reorders) is
  unchanged — the Templates page and structural edits still use it.
- `TemplateUpdateService` is untouched.

### 4. Tests (`manager/test/RCMM.Tests`)

- New `AdditionStore` migration tests:
  - v4 → v5 drops hand-authored entries, keeps template-derived ones.
  - Folders survive.
  - `SchemaVersion` lands on 5.
  - A pre-v3 file migrates all the way to v5 in one load (name-matched
    entries survive, unmatched hand-authored ones are dropped).
- Existing viewmodel/store tests that use hand-authored fixtures are
  adjusted to template-derived fixtures where hand-authored data would now
  be unrepresentative.

### 5. Error handling

Nothing new. The migration is a pure state transform inside the existing
`Load()` try/catch; a corrupt file still degrades to empty state.

### 6. Process

GitHub issue → `feat/` branch → PR referencing the issue, per repo rules.

## Out of scope

- Any change to the Browse Templates page or the template catalogue itself.
- Folding the Templates page into the Add page (considered, rejected as a
  bigger redesign than requested).
- A user-facing notice when hand-authored entries are dropped.
