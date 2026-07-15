# RCMM

Right-Click Menu Manager — a Windows utility for managing entries in the Explorer right-click context menu.

## What it is

WinUI 3 (Windows App SDK) + .NET 8, x64 only. Targets Windows 10 1809+ / Windows 11. Self-contained publish, no runtime install required. Distributed as an unsigned Inno Setup installer via GitHub Releases.

## Audience

Power users and tweakers. People who already know what a shellex or a verb is and want a faster GUI than `regedit`. UI doesn't shy away from technical labels when those are the truth — but renames noise like "Microsoft Security Client Shell Extension" to the actual menu text ("Scan with Microsoft Defender") wherever possible.

## UI direction

Modern dark utility: flat, compact, dark-only, no system chrome. Donut chart on the landing page, one unified filterable grid (origin + visibility chips) on the show-hide page. Stay in that direction. Don't introduce a light theme or Fluent/Mica without an explicit decision to change course.

## Scope

**In scope, present:**

- Discover every entry in the Windows Explorer right-click menu — classic verbs, packaged COM extensions, modern CommandStore verbs.
- Hide / unhide individual entries.
- Apply changes via registry; optionally restart Explorer when a change can only take effect that way.
- Add entries to the menu from the built-in template catalogue and structure them: drag order, folders (rendered as submenus, max 3 levels), name, and icon. Templates-only by design; there is no ad-hoc "write your own command" editor.

**In scope, planned** (not yet built):

- **Manage New >** — let users manage the "New >" submenu (new folder, .txt, .py, …) and register new file-type templates (e.g. `.md`).

Detailed plans, the "Browse templates" backlog, and audit follow-ups live in [`ROADMAP.md`](ROADMAP.md). Keep it current as work is planned and shipped.

**Out of scope**: Windows customization unrelated to the right-click menu — themes, taskbar, file explorer behavior, system settings, etc. RCMM stays a right-click-menu tool.

## Project layout

```
manager/src/RCMM.Core/           class lib — discovery, hide/apply, view-models
  Diagnostics/Log.cs             file logger: %LOCALAPPDATA%\RCMM\logs\rcmm.log (1 MB rotation)
  Interop/                       Win32 + shell COM definitions
  Models/                        records — CapturedItem, MenuEntry, HideTarget, PackagedShellExt, …
  Services/
    ContextMenuCaptureService    IContextMenu probe against sample target files
    PackagedShellExtScanner      PackagedCom\Package walk + AppxManifest logo extraction
    ClassicVerbScanner           HKCR\<scope>\shell verbs
    ClassicShellexScanner        HKCR\<scope>\shellex\ContextMenuHandlers
    EntryScanner                 aggregates the two classic scanners into CapturedItems
    CommandStoreVerbIndex        Windows.* modern verbs (Share, Copy as path, …) — CLSID lookup + friendly-name derivation
    ShellexInvoker               COM-probes handler CLSIDs for emitted names / IExplorerCommand::GetTitle / GetIcon
    HideService                  writes LegacyDisable / Blocked-list / HkcuMask hide markers
    VerbToRegistryMapper         per-verb → set of HideTargets
    Win32Registry                IRegistry adapter
    ExplorerRestart              kills + relaunches explorer.exe when a hide change needs it
  ViewModels/
    MainViewModel                orchestrator: Rescan, ApplyPending, rename + filter pipeline
    EntryRowViewModel            one row in the list
    ScopeListViewModel           backs ScopePage

manager/src/RCMM/                WinUI 3 app — views, shell, icon loader
  Util/IconHelper.cs             PNG bytes from ExtractIconEx / raw PNG / sibling-exe fallback
  Views/LandingPage.xaml*        donut chart + total
  Views/ShowHidePage.xaml*       App / Windows split cards
  Views/ScopePage.xaml*          the actual entry list
  MainWindow.xaml*               title bar, footer Apply button

manager/test/RCMM.Tests/         xUnit
installer/RCMM.iss               Inno Setup script
dist/                            publish/ + installer/ outputs (gitignored)
```

## How the app works

- **Rescan / discovery** — `MainViewModel.Rescan()` runs a 9-step capture → probe → rename → merge → dedupe → filter pipeline that produces the visible `AllEntries` list. Hide targets are `LegacyDisable` / `HkcuMask` / `BlockedShellExt`, applied via `HideService`; some need an Explorer restart. **Full step-by-step: the `rescan-pipeline` skill.**
- **Windows quirks** — Win11's classic vs modern menu, the legacy menu hack, and the packaged-COM `Directory\Background` cascade (plus RCMM's automatic `CascadeProtectionService` defense) all shape discovery and hide/apply. **Full detail + pitfalls: the `windows-context-menu` skill.**

## Build, test, release

`dotnet build` / `dotnet test` for the inner loop; self-contained x64 publish + Inno Setup installer to ship; version bump in `installer\RCMM.iss`; tag + `gh release`. **Full commands: the `build-release` skill.**

## Conventions

- C# file-scoped namespaces, `sealed` by default, records for value types.
- Comments explain **why**, not what. Long block comments on non-obvious pipeline decisions are welcome (see `MainViewModel.Rescan`, `ResolveIconPath`). Don't bloat trivial code.
- Logger: `RCMM.Core.Diagnostics.Log` with categories (`rescan`, `capture`, `cmdstore`, `shellexinvoker`, …). Output at `%LOCALAPPDATA%\RCMM\logs\rcmm.log`, rotates at 1 MB.
- No external NuGet dependencies beyond Windows App SDK / .NET BCL. Keep it that way.
- App manifest is `asInvoker`. Installer requires admin (writes to `Program Files`). Hide operations themselves are HKCU and don't need admin.

## Working with me

This project's intent and UX choices aren't all derivable from the code. When a request is ambiguous about scope, behavior, or what counts as "the right thing" — ask before assuming. Especially anything that:

- Adds an entry to the visible list or removes one (changes the rescan filter)
- Touches the rename / icon-resolution chain
- Drifts toward "general Windows customization" rather than right-click menu work

The scope is bounded; planned features are listed under **Scope** above. Don't infer features RCMM "should" have based on what similar tools do.

<!--
Claude Code setup (notes for human maintainers; stripped from Claude's context):
  .claude/skills/build-release/         build/test/publish/installer/version/release + screenshots
  .claude/skills/windows-context-menu/  classic vs modern menu, legacy hack, Background cascade, CascadeProtectionService
  .claude/skills/rescan-pipeline/       the 9-step MainViewModel.Rescan pipeline + hide-target kinds
  .claude/rules/discovery-and-rename.md path-scoped guardrails; auto-loads when editing discovery/rename/icon files
Reference detail lives in the skills (load on demand) to keep this file lean. See https://code.claude.com/docs/en/skills.
-->
