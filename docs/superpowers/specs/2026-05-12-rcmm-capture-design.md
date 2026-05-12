# RCMM Plan 2 — Capture-Based Classic Menu

**Date:** 2026-05-12
**Status:** Design approved, pending implementation plan
**Supersedes:** the registry-scan source-of-truth from `2026-05-12-rcmm-design.md` (Plan 1). Plan 1 services for hiding stay; Plan 1 scanners become internal helpers.

## Purpose

Replace Plan 1's registry-scanning source of truth with a **live menu capture** so RCMM lists exactly what Windows would actually display in the classic ("Show more options" or, with the classic-default registry flag, the regular) right-click menu. Nothing more, nothing less. Hide actions still go through the registry (verb `LegacyDisable`, HKCU shellex mask).

## Scope

### In scope
- Classic context menu capture via `IContextMenu` against a curated set of representative targets.
- Merge captures across targets into one deduplicated user-facing list.
- Map each captured item back to its registry contributor(s) so the existing hide path keeps working.
- "Everywhere" hide semantics — toggling one row applies `LegacyDisable` (or HKCU mask) at every registry location that contributes the item.
- Show un-hideable items (Cut/Copy/Paste/Rename/Delete/Properties) with a disabled toggle and a tooltip.
- Icons sourced from the captured menu's `MIIM_BITMAP` when available, with the existing DLL/exe fallback.

### Out of scope (Plan 3+)
- Modern Win11 menu (packaged `IExplorerCommand` providers + Blocked-CLSID hide).
- Adding new custom items (deferred from Plan 1).
- Backup snapshot + Undo all + Settings dialog (deferred from Plan 1).

## Confirmed decisions

| Topic                       | Choice                                                                                      |
| --------------------------- | ------------------------------------------------------------------------------------------- |
| Menu scope                  | Classic only (Plan 3 covers modern).                                                        |
| Source of truth             | Live `IContextMenu` capture, not registry scan.                                             |
| Target sampling             | ~10 representative targets: folder, drive, folder background, plus `.txt .png .mp4 .mp3 .pdf .zip .exe .lnk` file samples. |
| Hide breadth                | "Everywhere" — hide at every registry location the contributing verb/CLSID is registered.   |
| Un-hideable items           | Shown with a disabled toggle + tooltip explaining why.                                      |
| Default-show built-ins      | Yes (kept from Plan 1 fix). "Hide Windows built-ins" remains an opt-in checkbox.            |
| Threading                   | Dedicated STA worker thread for all COM/menu work; results marshaled to UI via DispatcherQueue. |

## Architecture

```
RCMM.Core/Interop/
  ShellInterop.cs              # P/Invoke + COM type defs (IShellItem, IContextMenu, IContextMenu2/3,
                               # SHCreateItemFromParsingName, CreatePopupMenu, GetMenuItemInfoW,
                               # GetMenuStringW, DestroyMenu, etc.)
RCMM.Core/Services/
  IContextMenuCaptureService.cs    # interface
  ContextMenuCaptureService.cs     # real impl (Win32 COM)
  TargetProvider.cs                # produces the 10 representative target paths (creates the
                                   # temp files/folder on first use, cleans up at exit)
  VerbToRegistryMapper.cs          # canonical verb → all HKCR\<scope>\shell\<verb> hide targets;
                                   # owner-CLSID → all HKCR\<scope>\shellex\ContextMenuHandlers\<key>
                                   # hide targets. Uses the existing registry scanners under the hood.
RCMM.Core/Models/
  CapturedItem.cs                  # one Win32 menu item from one capture
  MenuEntry.cs                     # deduplicated user-facing row (replaces Plan 1's ContextMenuEntry
                                   # as the UI binding type)
RCMM.Core/ViewModels/
  MainViewModel.cs                 # Rescan now drives ContextMenuCaptureService + VerbToRegistryMapper
```

Plan 1 services kept (existing code, no rewrite):
- `IRegistry`, `Win32Registry`, `FakeRegistry`
- `HideService` — still applies `LegacyDisable` / HKCU mask
- `ExplorerRestart`
- `ConfigStore`
- WinUI app shell, `ScopePage`, footer, theming, icon helper

Plan 1 services demoted to internal helpers, not part of the user-facing flow:
- `ClassicVerbScanner`, `ClassicShellexScanner`, `EntryScanner` — used only by `VerbToRegistryMapper` to find registry locations contributing a given verb/CLSID.
- `ClsidResolver`, `Win32FileVersionReader`, `Win32MuiStringResolver` — same.
- `EntryFilters` — removed (the capture is already the right list).

## Data model

```csharp
public sealed record CapturedItem
{
    public required string TargetPath { get; init; }      // which capture target produced this
    public required int Position { get; init; }
    public required string DisplayName { get; init; }      // post-resolve, post-accelerator-strip
    public string? Verb { get; init; }                     // canonical verb from GCS_VERBW; null for shellex
    public string? OwnerClsid { get; init; }               // best-effort, for shellex items
    public byte[]? IconBytes { get; init; }                // PNG bytes if menu carried a bitmap
    public bool IsSeparator { get; init; }
    public bool IsSubmenu { get; init; }
    public IReadOnlyList<CapturedItem> Children { get; init; } = Array.Empty<CapturedItem>();
}

public sealed record MenuEntry
{
    public required string Id { get; init; }              // stable, derived from Verb or OwnerClsid
    public required string DisplayName { get; init; }
    public string? Source { get; init; }                  // resolved company name from a contributor DLL
    public byte[]? IconBytes { get; init; }
    public required IReadOnlyList<HideTarget> HideTargets { get; init; }  // empty = un-hideable
    public bool IsBuiltIn { get; init; }                  // derived from contributor company name
    public bool IsHidden { get; init; }                   // true iff ALL HideTargets are currently masked
}

public sealed record HideTarget(HideKind Kind, RegistryHive Hive, string Path, string? ValueName);
public enum HideKind { LegacyDisable, HkcuMask }
```

## Capture pipeline

`ContextMenuCaptureService.CaptureAll()` runs on a dedicated STA worker thread. Lifecycle:

1. `CoInitialize(COINIT_APARTMENTTHREADED)` once per worker.
2. For each `targetPath` in `TargetProvider.GetTargets()`:
   a. `SHCreateItemFromParsingName(targetPath, null, IID_IShellItem)` → `psi`.
   b. `psi.BindToHandler(null, BHID_SFUIObject, IID_IContextMenu)` → `pcm`.
   c. `HMENU hMenu = CreatePopupMenu()`.
   d. `pcm.QueryContextMenu(hMenu, 0, idCmdFirst=1, idCmdLast=0x7FFF, CMF_NORMAL | CMF_EXTENDEDVERBS)`.
   e. Walk `hMenu`:
      - `n = GetMenuItemCount(hMenu)`
      - For `i` in `[0, n)`: `MENUITEMINFOW mii { fMask = MIIM_ID|MIIM_STRING|MIIM_BITMAP|MIIM_SUBMENU|MIIM_TYPE }`, `GetMenuItemInfoW(hMenu, i, true, &mii)`.
      - Resolve text: if `mii.fType & MFT_SEPARATOR` → record separator; if `mii.fType & MFT_OWNERDRAW` → owner-draw item, derive text via `IContextMenu2/3::HandleMenuMsg` if needed, else best-effort blank/CLSID.
      - Bitmap: if `mii.hbmpItem` is set, convert HBITMAP → PNG bytes.
      - Verb: `pcm.GetCommandString(mii.wID - idCmdFirst, GCS_VERBW, ...)`.
      - Submenu: if `mii.hSubMenu` is non-null, recurse.
   f. `DestroyMenu(hMenu)`. Release `pcm`, `psi`.
3. `CoUninitialize` on worker shutdown.

Merge step (on UI thread or worker, doesn't matter — pure data):
- For each `CapturedItem` produced, compute a merge key:
  - If `Verb` is non-empty → key = `verb:{verb}`.
  - Else if `OwnerClsid` is non-empty → key = `clsid:{clsid}`.
  - Else → key = `name:{normalized-display-name}`.
- First occurrence wins; subsequent captures with the same key are dropped from the user-facing list (but their target paths are remembered for hide breadth — see next section).

## Verb-to-registry mapping

`VerbToRegistryMapper.Map(CapturedItem)` produces zero or more `HideTarget`s:

For a captured item with `Verb = git_shell`:
1. Scan every scope root (`*`, `Directory`, `Drive`, `Directory\Background`, `AllFilesystemObjects`, `Folder`) for `HKCR\<root>\shell\git_shell`.
2. Each hit is a `HideTarget(LegacyDisable, HKCR, "<root>\shell\git_shell", "LegacyDisable")`.

For a captured item with `OwnerClsid = {ABC}`:
1. Scan every scope root for `HKCR\<root>\shellex\ContextMenuHandlers\*` whose default value or key name resolves to `{ABC}`.
2. Each hit is a `HideTarget(HkcuMask, HKCU, "Software\Classes\<root>\shellex\ContextMenuHandlers\<key>", null)`.

For items with no `Verb` and no `OwnerClsid` (or where the mapper finds zero hits): the resulting `MenuEntry.HideTargets` is empty, and the UI shows the toggle disabled with a "Built into Windows — can't hide" tooltip.

## Hide pipeline

Unchanged from Plan 1 in behavior, slightly extended for fan-out:

`HideService.Hide(MenuEntry entry)`:
- For each `target` in `entry.HideTargets`:
  - If `target.Kind == LegacyDisable`: `_reg.SetValue(target.Hive, target.Path, "LegacyDisable", "")`.
  - Else (`HkcuMask`): `_reg.CreateKey(target.Hive, target.Path); _reg.SetValue(target.Hive, target.Path, "", "")`.

`HideService.Unhide(MenuEntry entry)`:
- Mirror — `DeleteValue` for `LegacyDisable`, `DeleteKey` for the HKCU mask key.

`HideService.RequiresExplorerRestart(MenuEntry entry)`:
- True iff any `HideTarget` is of `HkcuMask` kind (matches Plan 1's shellex semantics).

The existing `HideService` is extended to accept a `MenuEntry`'s `HideTargets` list; the underlying registry writes are unchanged.

## UI

Same `ScopePage` from Plan 1, repointed at `MainViewModel.AllEntries` which now contains `MenuEntry`s (the deduped union of all captures). The row template:

- 28px icon — `MenuEntry.IconBytes` decoded to `BitmapImage`, fall back to the registry-side DLL icon via the existing `IconHelper` if `IconBytes` is null.
- Display name + subtitle `<Source> · <kind>` (kind reads "Verb" or "Shell extension" based on whether `Verb` or `OwnerClsid` is set).
- Toggle. If `HideTargets` is empty → toggle `IsEnabled=false` with tooltip "Windows hardcodes this entry; can't hide".
- "Built-in" badge driven by `IsBuiltIn` (set during mapping when contributor company contains "Microsoft" OR command lives under `%SystemRoot%`).

Footer, Apply button, theming, search box, "Hide Windows built-ins" checkbox — all unchanged from Plan 1.

## Error handling

- COM init / IContextMenu failures per target: caught, logged, that target's capture skipped (the others continue).
- A target path that doesn't exist (e.g., user has no D:): `TargetProvider` enumerates only paths that resolve; missing ones are silently skipped.
- Capture timeout: a misbehaving shell extension can hang inside `QueryContextMenu`. Mitigation: per-target captures run with a 5-second wait via `Task.Run(...).Wait(TimeSpan.FromSeconds(5))`. If the timeout fires, the in-flight COM call cannot be safely aborted on its STA thread; the worker thread is abandoned (`Thread.IsBackground = true` so it dies with the process) and a fresh STA worker is spun up for the next target. Acceptable cost: at most one hung extension per session.
- Empty capture result (e.g., all targets fail): RCMM shows an InfoBar "Couldn't capture the menu — see log" with a Retry button. The list is left empty rather than falling back to a stale registry scan.

## Testing strategy

`IContextMenuCaptureService` is the seam. Unit tests use `FakeContextMenuCaptureService` with a pre-seeded list of `CapturedItem`s, exactly like Plan 1's `FakeRegistry` pattern.

- `VerbToRegistryMapperTests` — uses `FakeRegistry` (already exists) to verify the verb-and-CLSID search across multiple scope roots.
- `MainViewModelTests` — rewritten to use `FakeContextMenuCaptureService` plus `FakeRegistry`, asserting merge/dedup, IsHidden derivation, ApplyPending fan-out across multiple HideTargets.
- `ContextMenuCaptureServiceTests` — real-COM integration smoke test executed only in interactive runs (not CI), asserting the capture returns at least the well-known verbs (`open`, `cut`, `copy`, `paste`, `properties`) against a temp file.

Plan 1's scanner tests (`ClassicVerbScannerTests`, `ClassicShellexScannerTests`, etc.) are kept; the production code those tests exercise is now reused by `VerbToRegistryMapper`.

## Migration

- `EntryFilters` and the `IsLikelyUserVisible` heuristic: deleted. The capture-based list doesn't need them.
- `MainViewModel.AllEntries` type changes from `ObservableCollection<EntryRowViewModel>` (wrapping `ContextMenuEntry`) to `ObservableCollection<EntryRowViewModel>` (wrapping the new `MenuEntry`). The wrapper VM gets a small refactor.
- `LandingPage` stays unused (already bypassed in Plan 1's final state). Will be retired or re-purposed in Plan 3 when the modern menu adds a second section.

## Risks

1. **Owner-draw menu items** (some shellex contributions use `MFT_OWNERDRAW` and don't expose text via `GetMenuStringW`). Mitigation: implement `IContextMenu2/3::HandleMenuMsg` to send `WM_MEASUREITEM`/`WM_DRAWITEM` to the contributor and recover the displayed text via a hooked DC. If this turns out to be too involved, fall back to displaying the OwnerClsid's `FileDescription` (best-effort) and flag the row as "name approximated".
2. **STA worker shutdown** — if the worker thread is torn down without `CoUninitialize`, COM may leak. Wrap with `try/finally`.
3. **Watchdog cost** — abandoning an STA thread mid-COM-call is the only safe option when a shell extension wedges. Each abandoned worker holds whatever resources the wedged extension grabbed until the process exits, so chronic timeouts are a leak. If this surfaces in practice, the longer-term fix is sandboxing the capture in a child process the manager can kill cleanly.
4. **First-launch performance** — 10 captures × maybe 100ms each + first-time COM init = ~2s on a warm machine, more on cold cache. Captures run on the STA worker after `MainWindow` is shown; each completed target adds its merged entries to `AllEntries` via `DispatcherQueue.TryEnqueue`, so the list grows progressively rather than blocking startup with a 2-second pause.
