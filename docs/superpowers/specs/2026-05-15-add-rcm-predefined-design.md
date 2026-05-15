# Add to RCM — predefined commands + user-organized entries (v1)

**Date**: 2026-05-15
**Status**: design approved; implementation pending
**Target version**: v0.5.0

## Summary

Add a new top-level page to RCMM, **"Add to menu"**, that lets the user add their own entries to the (classic) Windows right-click menu. Ships with 13 predefined developer-workflow templates (`npm run dev`, `git pull`, `dotnet build`, etc.) the user can opt in to. Users can also create their own entries from scratch and group everything into named folders (one level deep) which render as submenus in the actual context menu.

This is the second of two pillars in RCMM (the first being Show/Hide). Same Apply button, same registry-write model, same dark utility UI.

## Background and motivation

The right-click menu in Windows is a productivity surface: it's where one-handed actions live. Power users routinely run the same shell commands in the same kinds of folder — `npm run dev` in Node projects, `git pull` in repos, `dotnet build` in .NET projects. Adding these as menu entries is possible today by editing `HKCU\Software\Classes\…\shell\…` directly, but the registry surface is fiddly, undocumented at the level needed, and provides no scaffolding for organising entries into submenus.

RCMM already understands and writes Windows shell registry. Extending it to *create* entries (not just hide them) is the natural next pillar of the tool.

## Audience and constraints

- **Audience**: power users / tweakers (per `CLAUDE.md`).
- **Menu target**: the *classic* Windows context menu only (the one you reach via "Show more options" on Win11, or directly on Win10). Modern Win11 menu integration is out of scope — that would require a packaged COM extension implementing `IExplorerCommand`, which is a separate workstream.
- **Privilege**: HKCU-only. No admin elevation required for adding entries.
- **UI**: matches existing modern-dark-utility direction. No light theme, no Fluent/Mica.

## Scope

### In scope (v1)
- New **Add to menu** page in RCMM.
- **13 predefined templates** for common dev workflows (full list in §9).
- **Custom entries** the user creates from scratch.
- **Folders** (one level deep) that render as classic submenus via `ExtendedSubCommandsKey`.
- **Master-detail UI**: list of entries + folders on the left, editor on the right.
- **Templates browser** with one-click "Add" to clone a template into the user's setup.
- **Apply integration**: the existing footer Apply button now also commits Add changes (write `additions.json` atomically, then write registry).
- **Storage** in `%APPDATA%\RCMM\additions.json` (roaming).

### Out of scope (v1)
- True context filtering (file-presence detection in folder). Templates' "when X" labels are informational only — entries appear on every folder background. v2 would need a custom IExplorerCommand handler.
- Drag-and-drop reorganization (Folder dropdown only).
- Nested folders (one level only).
- HKLM / machine-wide entries (HKCU only).
- Template "updates from upstream" (clones are independent).
- Multi-scope per entry (single scope each; clone to get a second scope).
- Multi-line script files (.ps1/.bat/.py). Inline command strings only — user wanting a script writes their own and points the command at it.
- Variables / parameter prompts.
- Toast notifications / output capture.
- Import / export of `additions.json`.
- Predefined templates beyond the 13 dev commands (file utilities, system maintenance, transforms — all v2+).
- Cleanup-on-uninstall of the user's `RCMM.*` verbs (Inno Setup uninstaller leaves them; user data preserved).

## Data model

Two record types in `RCMM.Core.Models`:

```csharp
public sealed record AdditionEntry
{
    public required string Id { get; init; }           // GUID, generated when entry is created
    public required string Name { get; init; }         // display text shown in the menu
    public string? Icon { get; init; }                 // path to .ico/.exe/.dll/.png; null = derive from command's exe
    public required string Command { get; init; }      // bare command, e.g. "npm run dev" — RunMode controls wrapping
    public required string WorkingDir { get; init; }   // shell var, typically "%V"
    public required AdditionScope Scope { get; init; }
    public IReadOnlyList<string>? FileTypes { get; init; }  // for File scope only, e.g. [".png", ".jpg"]
    public string? FolderId { get; init; }             // null = top-level; else points to AdditionFolder.Id
    public required RunMode RunMode { get; init; }
}

public sealed record AdditionFolder
{
    public required string Id { get; init; }           // GUID
    public required string Name { get; init; }
    public string? Icon { get; init; }
}

public enum AdditionScope
{
    FolderBackground,   // Directory\Background\shell\         (right-click empty space in a folder)
    Folder,             // Directory\shell\                    (right-click on a folder from its parent)
    File,               // <ext>\shell\ or *\shell\            (right-click on a file)
    Drive,              // Drive\shell\                        (right-click on a drive root)
    AllFilesystemObjects // AllFilesystemObjects\shell\        (every file + folder)
}

public enum RunMode
{
    VisibleTerminal,    // wraps command as "cmd /k <command>" at registry-write time —
                        //   user sees the terminal, can read output, window stays open until they close it
    Background          // writes command as-is — caller's responsibility to make it windowless
                        //   (e.g. by using start /B, or pointing at a GUI executable directly)
}
```

**Templates are static** (compiled into `RCMM.Core.Services.AdditionTemplates`), not persisted in `additions.json`. When the user adds a template, a fresh `AdditionEntry` is created with the template's defaults; the link is severed and the new entry is fully editable.

## Storage

**Path**: `%APPDATA%\RCMM\additions.json` (roaming).

**Schema**:
```json
{
  "schemaVersion": 1,
  "folders": [
    { "id": "5f3a…", "name": "Dev tools", "icon": null }
  ],
  "entries": [
    {
      "id": "8c4b…",
      "name": "npm run dev",
      "icon": null,
      "command": "npm run dev",
      "workingDir": "%V",
      "scope": "FolderBackground",
      "fileTypes": null,
      "folderId": "5f3a…",
      "runMode": "VisibleTerminal"
    }
  ]
}
```

**Read**: once on RCMM launch (in `App.xaml.cs` startup wiring), feeds the `AdditionStore` service that the new view-models bind to.

**Write**: atomic `additions.json.tmp` + rename, triggered by the footer Apply button (same point as registry writes). Closing RCMM with unsaved Add changes prompts the user to confirm discard.

**Failure handling**: registry write happens first; only on success does `additions.json` get the rename. So a failed Apply leaves both file and registry on the previous state. Logged at `Log.Error("apply", …)`.

## Registry application

**Verb naming**: every RCMM-added registry key uses the prefix `RCMM.` followed by the entry's or folder's GUID — e.g. `RCMM.8c4b3f7a-1d2e-…`. Two reasons:
- A clean namespace RCMM can purge wholesale on every Apply (idempotency).
- Visually distinguishes RCMM-owned entries from anything else in the registry.

**Layout for a top-level entry** (Scope = `FolderBackground`, no folder):
```
HKCU\Software\Classes\Directory\Background\shell\RCMM.<entry-id>\
  (Default)            = entry.Name
  Icon                 = entry.Icon                 ; optional
  command\
    (Default)          = WrapForRunMode(entry)     ; "cmd /k npm run dev" for VisibleTerminal
                                                   ; bare "npm run dev" for Background
```

The `<scope>` segment in the path maps as defined in `AdditionScope`:

| Scope                  | Registry path                                   |
|------------------------|-------------------------------------------------|
| FolderBackground       | `Directory\Background\shell\…`                  |
| Folder                 | `Directory\shell\…`                             |
| Drive                  | `Drive\shell\…`                                 |
| AllFilesystemObjects   | `AllFilesystemObjects\shell\…`                  |
| File (no fileTypes)    | `*\shell\…`                                     |
| File (with fileTypes)  | one registration per ext: `<.ext>\shell\…`      |

`WorkingDir` and `Command` get passed through as shell-vars; we don't expand `%V` ourselves. Windows substitutes at invocation time.

**Layout for a folder + its children** (e.g. user creates "Dev tools" containing `npm run dev` and `git pull`, both FolderBackground):
```
HKCU\Software\Classes\Directory\Background\shell\RCMM.<folder-id>\
  (Default)               = "Dev tools"
  Icon                    = folder.Icon            ; optional
  ExtendedSubCommandsKey  = Directory\Background\ContextMenus\RCMM.<folder-id>

HKCU\Software\Classes\Directory\Background\ContextMenus\RCMM.<folder-id>\shell\
  RCMM.<entry1-id>\
    (Default) = "npm run dev"
    command\(Default) = "cmd /k npm run dev"            ; wrapped because RunMode=VisibleTerminal
  RCMM.<entry2-id>\
    (Default) = "git pull"
    command\(Default) = "cmd /k git pull"               ; same
```

`ExtendedSubCommandsKey` is the modern submenu mechanism. Path is relative to `HKCU\Software\Classes`. Works in HKCU.

**Mixed-scope folder**: if a folder contains entries with different scopes (e.g. one `FolderBackground`, one `Folder`), we write the folder verb under **every scope its children use**, with each parent's `ExtendedSubCommandsKey` pointing to a same-scope ContextMenus tree. The folder appears in the menu wherever any of its children would show.

**File-scope inside a folder**: if a folder contains a File-scope entry, the folder verb is written under `*\shell\` (or per-extension) and the entry appears as a child there. A user clicking the folder on a file sees only the file-scope children of that folder.

**Apply algorithm** (idempotent full-rewrite, simpler and safer than diff):

```
ApplyAdditions(currentState: { folders, entries }) {
  // 1. Tear down everything RCMM previously owned across all scopes.
  for each scope in [Directory\Background, Directory, Drive, AllFilesystemObjects, *]:
    delete every subkey under HKCU\Software\Classes\<scope>\shell\ whose name starts with "RCMM."
    delete every subkey under HKCU\Software\Classes\<scope>\ContextMenus\ whose name starts with "RCMM."
  for each extension (e.g. .png) referenced by a File-scope entry:
    delete every subkey under HKCU\Software\Classes\<ext>\shell\ whose name starts with "RCMM."

  // 2. Group entries by folder (or top-level).
  topLevel = entries.where(e => e.FolderId is null)
  byFolder = entries.where(e => e.FolderId is not null).groupBy(e => e.FolderId)

  // 3. Write top-level entries.
  for each entry in topLevel:
    for each scope-target in EntryScopePaths(entry):
      write HKCU\Software\Classes\<scope-target>\shell\RCMM.<entry.id>\
        (Default) = entry.Name
        Icon      = entry.Icon (if not null)
      write HKCU\Software\Classes\<scope-target>\shell\RCMM.<entry.id>\command\
        (Default) = entry.Command

  // 4. Write folder entries with their submenus.
  for each folder in folders:
    children = byFolder[folder.Id] ?? []
    scopesUsed = children.selectMany(c => EntryScopePaths(c)).distinct()
    for each scope in scopesUsed:
      write HKCU\Software\Classes\<scope>\shell\RCMM.<folder.id>\
        (Default)              = folder.Name
        Icon                   = folder.Icon (if not null)
        ExtendedSubCommandsKey = <scope>\ContextMenus\RCMM.<folder.id>
      for each child in children where EntryScopePaths(child) includes scope:
        write HKCU\Software\Classes\<scope>\ContextMenus\RCMM.<folder.id>\shell\RCMM.<child.id>\
          (Default) = child.Name
          Icon      = child.Icon (if not null)
        write …\command\(Default) = child.Command
}
```

Failure mode: `RegistryKey.DeleteSubKeyTree` can throw if a key is in use; we catch and log, abort the Apply, leave previous state intact. The user retries.

**Interaction with hide changes**: no overlap. Hide writes go to `…\shell\<verb>` `LegacyDisable` values or HKCU shellex masks under existing key names. Add writes go to `…\shell\RCMM.<guid>` under new key names. Different paths, different lifecycles. They share only the footer Apply trigger.

## UI shape

**Entry point**: the existing **"Add to menu"** card on the landing page (currently "Coming soon") becomes active. Click → navigate to the Add page.

**Page**: two-pane master-detail.

```
┌──────────────────────────────────────────────────────────────────┐
│ [+ New entry]   [+ New folder]   [Browse templates]              │
├───────────────────────────┬──────────────────────────────────────┤
│ ▾ Dev tools               │ Name        [ npm run dev          ] │
│   • npm run dev           │ Command     [ cmd /k npm run dev   ] │
│   • git pull              │ Working dir [ %V                   ] │
│ ▾ My scripts              │ Scope       [ FolderBackground   ▾ ] │
│   • Sync notes            │ Folder      [ Dev tools          ▾ ] │
│ • Open My Docs            │ Run mode    ( ● Visible  ○ Background)│
│                           │ Icon        [ (auto)               ] │
│                           │ [ Delete ]                           │
├───────────────────────────┴──────────────────────────────────────┤
│                          [ Apply (3) ]                           │
└──────────────────────────────────────────────────────────────────┘
```

- **Left**: collapsible tree of folders + top-level entries. Selecting populates the right pane. One level deep.
- **Right**: editor for selection. Entry editor fields: Name, Command, Working dir, Scope (dropdown), File types (visible only when Scope=File), Folder (dropdown of existing folders + "Top-level"), Run mode (radio), Icon, Delete. Folder editor: Name, Icon, Delete (with confirm: "Delete folder X? Entries inside will move to top-level").
- **Header**: three buttons: New entry, New folder, Browse templates.
- **Footer Apply**: pending count includes both hide and add changes. Single click commits both.

**Templates browser** (separate page reached via "Browse templates"):

```
┌──────────────────────────────────────────────────────────────────┐
│ [← back]   Browse templates                                      │
├──────────────────────────────────────────────────────────────────┤
│ Node                                                             │
│   [+] npm run dev               when package.json                │
│   [+] npm install               when package.json                │
│   [+] npm test                  when package.json                │
│ Git                                                              │
│   [+] git pull                  when .git/                       │
│   …                                                              │
└──────────────────────────────────────────────────────────────────┘
```

Grouped by ecosystem. The "when X" labels are informational only (no actual filter in v1). Clicking `[+]` clones the template into a fresh entry and jumps back to Add with that entry selected.

**Pending-changes semantics**: edits accumulate in memory (same pattern as today's hide toggles). Nothing is written until Apply. Closing the window with unsaved Add changes prompts a confirm.

**Reorganization** (v1): Folder dropdown in the entry editor. No drag-drop.

## Context filtering

**v1 reality**: classic verbs can't filter by "this folder contains file X". Every entry registered on `Directory\Background\shell\` appears on every folder. `npm run dev` shows up everywhere; clicking it in a non-Node folder produces an `npm ERR! Could not find package.json` in the terminal. Power users tolerate this — it's how Git Bash Here / Open with VS Code already behave.

**What classic filtering *does* support and we use**: scope (folder-bg vs file vs drive) and file extension. An entry with Scope=File and FileTypes=[".png"] correctly appears only on .png files.

**v2 plan** (not implementing now): an `IExplorerCommand` handler that does file-presence checks at menu-population time. Requires native COM, registration, antivirus surface area. The Entry model would gain a `RequireFileInFolder: string?` field that the handler reads from `additions.json`. Non-breaking schema bump.

## Predefined templates

All 13 ship with **Scope = FolderBackground**, WorkingDir = `%V`, RunMode = VisibleTerminal. They're "run in this directory" commands; none have a sensible file-scope variant.

| # | Name                          | Command                                  | Ecosystem | Informational "when…"        |
|---|-------------------------------|------------------------------------------|-----------|------------------------------|
Commands shown bare; RunMode=VisibleTerminal at registry-write time produces `cmd /k <Command>`.

| # | Name                          | Command                          | Ecosystem | Informational "when…"        |
|---|-------------------------------|----------------------------------|-----------|------------------------------|
| 1 | npm run dev                   | `npm run dev`                    | Node      | `package.json`               |
| 2 | npm install                   | `npm install`                    | Node      | `package.json`               |
| 3 | npm test                      | `npm test`                       | Node      | `package.json`               |
| 4 | git pull                      | `git pull`                       | Git       | `.git/`                      |
| 5 | git status                    | `git status`                     | Git       | `.git/`                      |
| 6 | git fetch --all               | `git fetch --all`                | Git       | `.git/`                      |
| 7 | dotnet build                  | `dotnet build`                   | .NET      | `*.csproj` / `*.sln`         |
| 8 | dotnet run                    | `dotnet run`                     | .NET      | `*.csproj`                   |
| 9 | python -m venv .venv          | `python -m venv .venv`           | Python    | `pyproject.toml` / `req.txt` |
| 10| pip install -r requirements   | `pip install -r requirements.txt`| Python    | `requirements.txt`           |
| 11| cargo run                     | `cargo run`                      | Rust      | `Cargo.toml`                 |
| 12| go run .                      | `go run .`                       | Go        | `go.mod`                     |
| 13| docker compose up             | `docker compose up`              | Docker    | `compose.yaml`               |

## Implementation principles (per session directive)

**Structured iteration**: implementation breaks into discrete steps via `writing-plans`. Each step gets its own task. No step is "done" until its tests pass and the integration smoke test still works.

**Logging**: every new code path adds `Log.Info` / `Log.Debug` calls with category strings (`additions`, `addapply`, `templates`, `addstore`). Categories let me filter the existing `%LOCALAPPDATA%\RCMM\logs\rcmm.log` to follow one subsystem.

**Tests**:
- Unit-test every pure-logic class (`AdditionStore` JSON round-trip, registry-write planner, scope mapping, prefix-purge logic).
- Tests are written **failing first**, then made to pass.
- Edge cases get their own tests: empty store, folder with no children, child with scope not used by any other child of the same folder, entry with `FileTypes` listing multiple extensions, deleting a folder that still has entries assigned, special characters in entry names (must be safe for registry value).
- Existing 79 tests must still pass after every change.

**Task-achieved definition**: a task is "achieved" when (a) its specific tests are green, (b) the full test suite is green, (c) RCMM builds without new warnings, (d) where applicable, a manual smoke test of the running app shows the expected behaviour (entries appear, edits apply, registry contains the expected `RCMM.*` keys, Windows context menu shows them).

**Stop condition**: not until every task on the implementation plan meets that definition.

## Risks

1. **`DeleteSubKeyTree` failures during Apply**. If a child key is in use (rare for HKCU), the whole tear-down can throw partway and leave a mix of old and new state. Mitigation: catch per-key, log, continue. Worst case the next Apply finishes the cleanup.
2. **Scope expansion when a folder spans scopes**. The "register parent verb under every scope used by any child" rule is right but multiplies the registry surface. Probably fine for typical sizes (≤30 entries) but worth keeping in mind.
3. **Verb name length**. Registry key names are bounded; `RCMM.<guid>` is 41 chars which is well within limits.
4. **JSON corruption** on crash mid-write. Mitigated by `.tmp` + rename. Worst case the file is rolled back to the previous good state.
5. **Long `additions.json`** as user adds many entries. JSON keeps loading cost trivial up to thousands of entries; not a real concern.
