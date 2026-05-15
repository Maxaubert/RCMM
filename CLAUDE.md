# RCMM

Right-Click Menu Manager — a Windows utility for managing entries in the Explorer right-click context menu.

## What it is

WinUI 3 (Windows App SDK) + .NET 8, x64 only. Targets Windows 10 1809+ / Windows 11. Self-contained publish, no runtime install required. Distributed as an unsigned Inno Setup installer via GitHub Releases.

## Audience

Power users and tweakers. People who already know what a shellex or a verb is and want a faster GUI than `regedit`. UI doesn't shy away from technical labels when those are the truth — but renames noise like "Microsoft Security Client Shell Extension" to the actual menu text ("Scan with Microsoft Defender") wherever possible.

## UI direction

Modern dark utility: flat, compact, dark-only, no system chrome. Donut chart on the landing page, App / Windows split on the show-hide page. Stay in that direction. Don't introduce a light theme or Fluent/Mica without an explicit decision to change course.

## Scope

**In scope, present:**

- Discover every entry in the Windows Explorer right-click menu — classic verbs, packaged COM extensions, modern CommandStore verbs.
- Hide / unhide individual entries.
- Apply changes via registry; optionally restart Explorer when a change can only take effect that way.

**In scope, planned** (not yet built):

- **Add to RCM** — let users add their own entries to the right-click menu: run a script (ad-hoc or from a predefined list), launch a program, etc.
- **Manage New >** — let users manage the "New >" submenu (new folder, .txt, .py, …) and register new file-type templates (e.g. `.md`).

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

## Rescan pipeline

`MainViewModel.Rescan()`:

1. **Live capture** — `ContextMenuCaptureService` invokes `IContextMenu` against sample targets (`.txt`, `.png`, `.mp4`, `.exe`, `.lnk`, `.zip`, a folder, `C:\`).
2. **Packaged scan** — `PackagedShellExtScanner` reads `HKLM\Software\Classes\PackagedCom` and each package's `AppxManifest.xml` (for the Logo asset path).
3. **Registry scan** — `EntryScanner.ScanAsCaptures()` walks classic verbs + shellex across six scopes (Files, Folders, Drives, Background, AllObjects, Folder).
4. **Single invoker probe** — every known CLSID (live + packaged + registry + CommandStore + verb-handler fields) is registered with `ShellexInvoker` *before* its one `BuildDisplayNameToClsidMap` call. Probes COM once and caches; subsequent rescans are free.
5. **Pre-merge rename** — rows whose DisplayName matches `LooksTechnical` get renamed via, in order: (a) `IShellExtInit + IContextMenu` emitted names, (b) `IExplorerCommand::GetTitle`, (c) CommandStore verb-name derivation (e.g. `Windows.ModernShare` → "Share"), (d) a small static override table for handlers that don't probe (Defender, NVIDIA).
6. **Merge** — by Id: `verb:<verb>` | `clsid:<clsid>` | `name:<display>`.
7. **Build rows** — resolve hide targets, icon path, source, IsBuiltIn (Windows-folder DLL **and** Microsoft publisher).
8. **Dedupe by DisplayName** — collapse rows with identical names; prefer live captures over registry-derived rows.
9. **Filter into `AllEntries`** — drop suppressed names, technical-looking names, and clsid-only rows that aren't packaged / observed / renamed / CommandStore-known.

Hide targets are one of: `LegacyDisable` value, `HkcuMask` key (an HKCU shadow of an HKLM key), `BlockedShellExt` entry. Apply writes these via `HideService`; some kinds need an Explorer restart to take effect.

## Windows quirks RCMM has to navigate

### Classic vs modern menu (Win11)

Windows 11 ships two parallel menus. The **modern flyout** is the short one with icons on a top row; it sources items from packaged COM extensions implementing `IExplorerCommand` (the "Open in Terminal" hook, "AMD Software" submenu, etc., registered via an AppX manifest's `windows.fileExplorerContextMenus`). The **classic menu** is what you get from "Show more options" and what `IShellFolder::CreateViewObject(IID_IContextMenu)` returns; it surfaces classic `HKCR\<scope>\shell\<verb>` verbs and `IContextMenu` shellex extensions. **Packaged-COM `IExplorerCommand` extensions do not appear in the classic menu.**

The well-known **legacy menu hack** forces the classic menu to be the default by neutering the modern-menu CLSID for the current user:

```
HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32\(Default) = ""
```

This is a deliberate power-user choice, not damage — RCMM must coexist with it. Symptom when the hack is on: every packaged-COM extension that has no classic verb fallback (Terminal's "Open in Terminal", AMD's "AMD Software", etc.) is invisible. RCMM's `PackagedShellExtScanner` still finds them in `HKLM\Software\Classes\PackagedCom`, but the live `IContextMenu` capture won't see them — that's expected, not a bug.

### The packaged-COM Directory\Background cascade

Adding a packaged-COM extension's CLSID to `HKCU\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked` can knock out **other** packaged extensions registered for the same `Directory\Background` ItemType in the modern flyout. Reported case: hiding AMD Radeon Software (`{6767B3BC-...}`) via RCMM also removed "Open in Terminal" (`{9F156763-...}`) from the folder-background flyout. The reverse direction has not been verified — assume any Background-scoped packaged-COM hide may cascade.

Symptoms make recovery feel impossible: the user un-toggles the hide in RCMM (RCMM removes its `Blocked` entry), but Explorer keeps the other extensions invisible until something else perturbs the packaged-COM cache. Restarting Explorer alone isn't always enough; the surviving workaround is to give the at-risk extension a **classic verb fallback** under `HKCU` so it lives in the registry independent of packaged-COM activation:

```
HKCU\Software\Classes\Directory\Background\shell\<Name>\(Default) = "Open in &Terminal"
HKCU\Software\Classes\Directory\Background\shell\<Name>\Icon      = "<exe>,0"
HKCU\Software\Classes\Directory\Background\shell\<Name>\NoWorkingDirectory = ""
HKCU\Software\Classes\Directory\Background\shell\<Name>\command\(Default) = "\"<exe>\" -d \"%V\""
```

…and the same under `Directory\shell\<Name>` (use `%1` instead of `%V` for the directory-as-item scope). For UWP-only apps (AMD Adrenaline), the command is `explorer.exe shell:AppsFolder\<PackageFamilyName>!<ApplicationId>`. These are user-scope and reversible with `reg delete`.

RCMM defends against this automatically. `PackagedShellExtScanner` parses each package's `AppxManifest.xml` to learn which `ItemType`s each CLSID is registered for and to derive an AUMID (`PackageFamilyName!ApplicationId`). `PackagedShellExt.IsBackgroundExtension` is true when the manifest binds the CLSID to `Directory\Background`. Before `MainViewModel.ApplyPending` writes any Background-scoped CLSID to `Shell Extensions\Blocked`, `CascadeProtectionService.PlanProtections` enumerates the OTHER Background packaged extensions and emits classic-verb fallbacks at `HKCU\Software\Classes\Directory(\Background)\shell\RcmmProtect_<clsid-without-braces>` whose `command` is `explorer.exe shell:AppsFolder\<AUMID>`. After every unhide, if no Background packaged CLSID remains in `Blocked`, `CascadeProtectionService.UninstallAll` sweeps the `RcmmProtect_` verbs back out. User-authored classic verbs (e.g. `OpenInTerminal`, `AMDSoftware`) are untouched because the sweep is namespace-scoped to the `RcmmProtect_` prefix.

Two pitfalls baked into the implementation after first-iteration mistakes:

- **Skip protection when legacy menu hack is active.** The cascade only manifests in the modern flyout's `IExplorerCommand` enumeration. With the legacy hack on (HKCU `…\CLSID\{86ca1aa0-…}\InprocServer32` exists), the user never sees the modern menu, so the protection verbs would just clutter the classic menu with raw packaged-COM placeholders. `PlanProtections` early-returns when `IsLegacyMenuModeActive()` is true.
- **Use `PublisherDisplayName`, not `DisplayName`.** A packaged COM Server's `DisplayName` is the technical class name ("Catalyst Context Menu extension", "WindowsTerminalShellExt"); its `ApplicationDisplayName` (surfaced as `PackagedShellExt.PublisherDisplayName`) is the friendly app name ("AMD Software", "Terminal"). The protection verb's `(default)` is what Explorer renders — using the technical name produced exactly the ugly placeholders this feature was meant to avoid.
- **Don't fall through `LogoPath` → `DllPath` for Icon.** A WindowsApps DLL usually has zero icon resources, so writing it as `Icon` produces an iconless menu item. When `LogoPath` doesn't resolve, leave `Icon` unset and let Explorer fall back to a default.

Tests: `CascadeProtectionServiceTests`, `PackagedManifestParserTests`.

## Build, test, release

```powershell
# Build (debug)
dotnet build manager\src\RCMM\RCMM.csproj

# Run tests
dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj

# Self-contained x64 publish + Inno Setup installer
dotnet publish manager\src\RCMM\RCMM.csproj -c Release -r win-x64 --self-contained true `
  -p:Platform=x64 -p:WindowsAppSDKSelfContained=true -p:WindowsPackageType=None `
  -o dist\publish
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\RCMM.iss
```

Outputs:

- `dist\publish\` — self-contained Release tree (input to ISCC).
- `dist\installer\RCMM-Setup-x64-<version>.exe` — the shipped installer.

**Version bump**: edit `installer\RCMM.iss` → `#define MyAppVersion "X.Y.Z"`. Bump this when releasing a new version so Windows' Add/Remove Programs perceives an upgrade.

**Release**: `git tag -a vX.Y.Z -m "vX.Y.Z" && git push --tags`, then `gh release create vX.Y.Z dist/installer/RCMM-Setup-x64-X.Y.Z.exe --title "RCMM vX.Y.Z" --notes "..."`.

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
