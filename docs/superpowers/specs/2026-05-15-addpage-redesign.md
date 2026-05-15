# Add page redesign — drag-drop reorder, nested folders, icon picker

The interactive mockup at `.superpowers/brainstorm/1997-1778879189/content/interactive-replica-v2.html` is the visual spec. This doc captures the data-model and behavioural decisions only.

## What changes

The Add page becomes a Finder-style three-pane editor with drag-and-drop, nested folders (max depth 3), and a bundled icon library. Today's flat list + 2-pane editor is replaced.

## Decisions (confirmed during brainstorming)

- **Layout: A — adaptive.** Two panes when an entry is selected (today's UI), three panes when a folder is selected (list · folder contents · folder properties).
- **Folder nesting: B — nested, capped at 3 levels deep.** A folder may contain folders. The deepest right-click chain is `RClick → A → B → C → entry`.
- **Drill-in (A).** Clicking a child folder in the middle pane navigates into it; breadcrumbs in the middle pane head, back button on the far left of the breadcrumb row.
- **Drag-and-drop semantics (post-iteration):**
  - Drop on top/bottom edge of a row → reorder; **cross-bucket allowed** (drop above a root row from inside a folder = promote to root).
  - Drop on a folder row's body (middle 50%) → move dragged into that folder.
  - Drop on the middle pane background → move dragged into the currently-displayed folder.
  - Depth cap enforced on all of the above; rejected drops show a red toast.
  - No multi-select drag, no external-file drop in v1.

## Data-model deltas

- `AdditionFolder`:
  - **add** `ParentFolderId : string?` — null = top-level. Enables nesting.
  - **add** `Scope : AdditionScope` — folder's own scope; defaults to `FolderBackground`. Children inherit if their own Scope is set to the same; today's flat-list cross-scope mixing is preserved (folder verb is written at the union of children's scopes, as today).
- `AdditionEntry`: unchanged.
- `AdditionState.SchemaVersion`: `1 → 2`. Migration: every existing folder gets `ParentFolderId = null` and `Scope = FolderBackground`. Existing JSON files load and re-save into v2 without user action.

Order is captured implicitly by the position of each item inside the `Folders` and `Entries` lists at save time. No explicit `Order` field — ObservableCollection moves are the source of truth.

## AdditionApplier deltas

- **Nested folders.** A folder with `ParentFolderId != null` is written as a sub-verb under its parent's `ExtendedSubCommandsKey` ContextMenus subtree, recursively. The top-level verb still lives at `HKCU\Software\Classes\<scope>\shell\RCMM.<id>`; nested folder verbs live at `HKCU\Software\Classes\<scope>\ContextMenus\RCMM.<parentId>\shell\RCMM.<id>`, each with their own `ExtendedSubCommandsKey` pointing further down.
- **Per-scope union still applies.** A folder is written under every scope at least one of its (transitive) descendant entries registers under. A folder with a single child only registered for `File:*.png` only appears in the .png file menu.
- **Order via key-name prefix.** Verb keys are written as `RCMM.<padded-ordinal>.<id>` where `<padded-ordinal>` is a 3-digit number reflecting the item's position within its bucket. Windows orders classic verbs alphabetically by key name, so the prefix forces the user's chosen order. The `RCMM.` prefix is preserved so the existing purge logic (`PurgePrefixed`) still owns these keys cleanly.
- **Idempotent rebuild stays.** Purge → re-write from a snapshot.

## UI deltas

The mockup describes everything visual. Key invariants for the C# port:

- **Theme colors:** unchanged — use existing `AppText`/`AppSurface`/`AppBorder`/`AppAccent` resources.
- **List rows:** no default icons; show a 16px library-icon SVG when the row's `Icon` is `lib:<name>`.
- **Twist column:** always reserved (16px) on both folder and entry rows so labels line up at the same depth.
- **Drag handles:** appear on hover only; the whole row is draggable.
- **Drop indicators:** 2px lime line between rows for reorder; row-fill highlight for drop-into-folder; full-pane border highlight for drop-into-middle-pane.

### Icon library

~40 Lucide-style outline icons bundled as XAML resources (vector). Storage convention in the `Icon` field:
- `lib:<name>` — library icon
- anything else — passed through to Windows as a raw path / `path,index` string (advanced)

The editor's Icon field becomes a picker widget: 44px preview + "Choose icon" button + "Clear" link + a custom-path advanced row.

The picker opens a `ContentDialog` with a search box and a grid of icons grouped by category (Shells & code, Files & folders, Editing, Tools, Run & power, Identity & security, Programming, Misc).

## Out of scope (v1)

- Multi-select drag.
- External-file drop (no dragging from File Explorer).
- Folder-level Scope constraining children (today's implicit union from children stays).
- Custom user-uploaded icons.
- Per-scope ordering (one global order across scopes, like today).

## Tests

Core changes (Models, Store, Applier, ViewModel ops) get xUnit tests:

- `AdditionStoreTests`: round-trip v2 JSON; migrate v1 → v2 on load.
- `AdditionApplierTests`: nested folder verb tree; verb names get ordinal prefix; idempotent rebuild after reorder.
- `AddPageViewModelTests`: move entry between buckets, reorder within bucket, depth-cap rejection.

UI changes verified by running RCMM and screenshot-comparing the Add page against the mockup.
