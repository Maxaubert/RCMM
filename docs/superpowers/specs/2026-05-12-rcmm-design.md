# RCMM — Right-Click Menu Manager: Design

**Date:** 2026-05-12
**Status:** Design approved, pending implementation plan

## Purpose

A Windows 11 utility that lets users curate their right-click (context) menu: hide unwanted items added by installed apps, and add their own custom items. UI/feel matches Hide-Any-Window (WinUI 3, theme-aware, per-row toggles).

## Scope

### In scope (v1)
- Read and display every classic-menu entry (verbs and shell extensions) across the file/folder/drive/desktop-background scopes.
- Read and display blockable packaged shell extensions that appear in the Windows 11 modern menu.
- Hide / unhide entries via reversible, well-documented registry mechanisms.
- Add custom items to the classic menu (Settings deep-links, run-a-program, run-a-script, open-a-folder, free-form).
- Backup of touched registry state, plus per-change tracking, with an "Undo all" path.

### Out of scope (v1)
- Adding items to the modern Windows 11 menu (requires shipping a packaged `IExplorerCommand` shell extension — deferred).
- A long-running background service (the OS reads the registry on demand; no daemon needed).
- Cross-machine sync.

## Confirmed decisions

| Topic                     | Choice                                                                                  |
| ------------------------- | --------------------------------------------------------------------------------------- |
| Menu scope for hiding     | Both modern (Win11) and classic ("Show more options") menus.                            |
| Adding scope              | Classic menu only. Modern-menu additions deferred.                                      |
| Elevation                 | App always launches elevated (manifest `requireAdministrator`). One UAC prompt per run. |
| Landing UI                | Hub of cards; one card per scope; clicking drills into that scope's list.               |
| Built-in Windows entries  | Shown with a "Built-in" badge. First toggle-off per scope triggers a confirmation.      |
| Apply behavior            | Batched. Footer "Apply (restart Explorer)" button, accented when restart is pending.    |
| Backup                    | Full `.reg` snapshot of touched hives on first launch + per-change JSON tracking.       |
| Custom-add UX             | Templates tab (fixed presets + parameterized) plus a blank free-form tab.               |
| App name / repo folder    | RCMM. Root at `C:\Users\Admin\Documents\Claude\Github\RCMM`.                            |

## Architecture

Single WinUI 3 / .NET 8 desktop app. No service. Repo layout mirrors Hide-Any-Window minus `service/`:

```
RCMM/
  manager/                  # WinUI 3 app (solution + src)
  dist/                     # icon, build script, Inno Setup
  docs/                     # this spec lives here
  README.md
```

Persistent state lives at `%APPDATA%\RCMM\`:

```
%APPDATA%\RCMM\
  config.json               # entries, changes, custom additions, backup records
  snapshots\initial.reg     # full export of touched hives on first launch
  log.txt                   # structured log
```

All registry writes are scoped to `HKCU` where possible (custom adds, classic shellex masking). HKLM writes (modern-menu blocking, system-wide hide) ride the always-elevated process.

## Data model

```csharp
public sealed class ContextMenuEntry {
    string Id;                  // stable, derived from registry path
    string DisplayName;
    string Source;              // e.g., "WinRAR", "Windows", "Custom"
    Scope Scope;                // Modern | Files | Folders | Drives | Background
    EntryKind Kind;             // ShellVerb | ShellExtension | PackagedHandler
    string RegistryPath;
    string OriginalKeyName;
    string IconPath;
    string CommandLine;         // for verbs
    string Clsid;               // for shell extensions / packaged
    bool IsBuiltIn;
    bool IsUserAdded;
    bool IsHidden;
    bool ShiftOnly;             // "Extended" value present
}

public sealed class BackupRecord {
    string EntryId;
    string Action;              // "LegacyDisable" | "HkcuMask" | "BlockedCLSID" | "AddVerb"
    string RegistryPath;
    string ValueName;
    string OriginalType;        // REG_SZ, REG_DWORD, etc.
    string OriginalValue;       // serialized original (null if value didn't exist)
    DateTime Timestamp;
}

public sealed class Config {
    int SchemaVersion = 1;
    List<ContextMenuEntry> KnownEntries;
    List<BackupRecord> Backup;
    UiPrefs Ui;                 // last scope viewed, theme override, etc.
}
```

## UI

### Landing page — hub of cards
2- or 3-column grid of cards on the main window:

- Modern menu
- Files
- Folders
- Drives
- Desktop & folder background
- Custom additions (filtered view: `IsUserAdded == true` across all real scopes)

Each card shows its title, a short subtitle (e.g., "5 hidden of 23"), and an icon. Click drills into the corresponding list view.

### Scope view
Same row pattern as Hide-Any-Window:
- 28×28 icon (resolved from DLL `FileDescription`, verb `Icon` value, or a fallback initial tile).
- Name with `<Source> · <Kind>` subtitle.
- Toggle switch (on = visible in menu, off = hidden).
- Delete button only for `IsUserAdded` rows.
- Grey "Built-in" badge next to name for entries shipped by Windows.

Top strip: back button, scope title, search box, `+ Add` button (suppressed on the Modern menu view; visible on classic scopes and prominent on Custom additions).

### Footer
- Left: counts ("23 entries · 5 hidden · 2 pending"), small status text.
- Right: `Apply (restart Explorer)` button, accented when there are pending changes that need a restart.

### Settings dialog
- **Re-scan** — rebuild entry list.
- **Show built-in Windows entries** — toggle (default on; user can hide them from the list entirely).
- **Undo all changes** — replays backup records in reverse.
- **Re-import initial snapshot** — nuclear restore from `initial.reg`.
- **Open config folder** — opens `%APPDATA%\RCMM\` in Explorer.

### Custom add dialog
Three tabs:
1. **Templates** — fixed presets: Night Light, Display, Sound, Network, Bluetooth, Mouse, Power & battery, About (each a `ms-settings:` deep-link). One-click add into chosen scope.
2. **Parameterized** — Open folder…, Run program…, Run script…  User picks a target + name + scope + optional Shift-only.
3. **Custom** — full form: Name, Icon, Command, Working directory, Scope checkboxes, Shift-only toggle.

## Mechanics

### Hide / unhide

| Entry kind                     | Hide mechanism                                                                                                            | Unhide                                                       |
| ------------------------------ | ------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------ |
| Classic verb (`shell\<verb>`)  | Add `LegacyDisable` (REG_SZ, empty) value on the verb's key.                                                              | Delete the `LegacyDisable` value.                            |
| Classic shellex handler        | Write `HKCU\Software\Classes\<scope>\shellex\ContextMenuHandlers\<Name>` default = "" to mask the HKCR-inherited handler. | Delete our HKCU masking key.                                 |
| Modern packaged handler        | Add the handler's CLSID as a REG_SZ value (empty data) under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked`. | Delete that value. |

Every operation records a `BackupRecord` *before* writing.

### Add custom (classic only)
Writes under `HKCU\Software\Classes\<scope>\shell\<sanitized-id>`:
- `(default)` → display name
- `Icon` → icon path (REG_SZ or REG_EXPAND_SZ)
- `Extended` → present (REG_SZ, empty) iff Shift-only
- `command\(default)` → command line; `%1` placeholder for selected target on Files/Folders/Drives; no placeholder on Background.

Entry stored in `KnownEntries` with `IsUserAdded = true` and `Source = "Custom"`.

### Scanning
On launch and on manual refresh:

1. Walk classic hives:
   - `HKCR\*\shell` and `HKCR\*\shellex\ContextMenuHandlers`
   - `HKCR\Directory\shell` and `...\shellex\ContextMenuHandlers`
   - `HKCR\Directory\Background\shell` and `...\shellex\ContextMenuHandlers`
   - `HKCR\Drive\shell` and `...\shellex\ContextMenuHandlers`
   - `HKCR\Folder\shellex\ContextMenuHandlers`
   - `HKCR\AllFilesystemObjects\shellex\ContextMenuHandlers`
2. Walk modern handler registries:
   - `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved`
   - `HKCU\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved`
   - PackagedCom entries (`HKCU\Software\Classes\PackagedCom\...`) — match against approved CLSIDs.
3. For each CLSID, resolve via `HKCR\CLSID\<CLSID>\InprocServer32` → DLL path → read `FileDescription` and `CompanyName` for `Source`, extract icon for `IconPath`.
4. Detect `IsHidden` state by checking the same mechanisms used in hide/unhide.
5. Merge with `KnownEntries` from config so user-added entries and our notes persist across rescans.

### Apply
- Changes are staged in memory and persisted to `config.json` (debounced ~200ms after edit) as the user toggles.
- `LegacyDisable` writes take effect on the next menu open; no restart needed.
- Shellex masking and Modern Blocked changes need Explorer restart. When any of those are pending, the footer's `Apply` button becomes accented.
- Apply runs `taskkill /f /im explorer.exe` then `start explorer`.

## Backup & restore

- First-ever launch: enumerate the touched hives and run `reg.exe export <hive> snapshots\initial.reg` for each. Concatenate into a single `.reg` if practical, or keep one per hive. Never overwritten on subsequent runs.
- Every registry write: append a `BackupRecord` to `config.Backup` *before* mutating.
- Settings dialog exposes:
  - **Undo all** → walk `Backup` in reverse, restore values, delete user-added entries.
  - **Re-import initial snapshot** → `reg import initial.reg`, then re-scan.

## Error handling

- All registry I/O routed through `IRegistry` abstraction; concrete impl wraps `Microsoft.Win32.Registry` in try/catch and converts to a `RegistryResult` discriminated union (`Ok | NotFound | AccessDenied | UnknownError`).
- Failed writes do not update `KnownEntries` or `Backup`; surface as a non-modal `InfoBar` with a "Retry" action.
- Malformed `config.json`: load empty config, archive corrupted file as `config.json.bak.<timestamp>`.
- Structured logging to `%APPDATA%\RCMM\log.txt` (rolling, 1MB cap).

## Testing

- Unit tests against the `IRegistry` interface using an in-memory fake. Cover: hide/unhide for each entry kind, add custom (all template flavors), backup record creation, undo replay, scan-merge with existing config.
- Integration smoke test under a sandbox key (`HKCU\Software\RCMM-Test\Sandbox\...`) exercising the real Win32 registry APIs; runs only in CI / dev, never touches live shell hives.
- Manual QA checklist (in `docs/qa-checklist.md`): toggles take effect on real Explorer, Apply restart works, Undo all returns to baseline, custom adds appear in classic menu.

## Risks & open questions

1. **Modern menu coverage** — only packaged extensions registered via `IExplorerCommand` can be blocked. Microsoft's built-in top-row items (Cut/Copy/Paste/Rename/Share/Delete) are baked into shell32 and not blockable through the Blocked CLSID mechanism. UI must communicate this — empty Modern view if nothing is blockable.
2. **Explorer restart UX** — restart closes open File Explorer windows. Warn once with a "don't show again" toggle.
3. **Future modern-menu additions** — keep architecture flexible enough to drop in a packaged `IExplorerCommand` shell extension in v2 without rewriting the data model. Schema versioning in config supports migration.
