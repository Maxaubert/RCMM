---
name: windows-context-menu
description: Reference for the Windows context-menu quirks RCMM must navigate — classic vs modern (Win11) menus, the legacy menu hack, the packaged-COM Directory\Background cascade, and RCMM's CascadeProtectionService defense. Use when working on discovery, hide/apply, packaged-COM scanning, cascade protection, or diagnosing why a menu entry is missing or duplicated.
---

# Windows quirks RCMM has to navigate

## Classic vs modern menu (Win11)

Windows 11 ships two parallel menus. The **modern flyout** is the short one with icons on a top row; it sources items from packaged COM extensions implementing `IExplorerCommand` (the "Open in Terminal" hook, "AMD Software" submenu, etc., registered via an AppX manifest's `windows.fileExplorerContextMenus`). The **classic menu** is what you get from "Show more options" and what `IShellFolder::CreateViewObject(IID_IContextMenu)` returns; it surfaces classic `HKCR\<scope>\shell\<verb>` verbs and `IContextMenu` shellex extensions. **Packaged-COM `IExplorerCommand` extensions do not appear in the classic menu.**

The well-known **legacy menu hack** forces the classic menu to be the default by neutering the modern-menu CLSID for the current user:

```
HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32\(Default) = ""
```

This is a deliberate power-user choice, not damage — RCMM must coexist with it. Symptom when the hack is on: every packaged-COM extension that has no classic verb fallback (Terminal's "Open in Terminal", AMD's "AMD Software", etc.) is invisible. RCMM's `PackagedShellExtScanner` still finds them in `HKLM\Software\Classes\PackagedCom`, but the live `IContextMenu` capture won't see them — that's expected, not a bug.

## The packaged-COM Directory\Background cascade

Adding a packaged-COM extension's CLSID to `HKCU\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked` can knock out **other** packaged extensions registered for the same `Directory\Background` ItemType in the modern flyout. Reported case: hiding AMD Radeon Software (`{6767B3BC-...}`) via RCMM also removed "Open in Terminal" (`{9F156763-...}`) from the folder-background flyout. The reverse direction has not been verified — assume any Background-scoped packaged-COM hide may cascade.

Symptoms make recovery feel impossible: the user un-toggles the hide in RCMM (RCMM removes its `Blocked` entry), but Explorer keeps the other extensions invisible until something else perturbs the packaged-COM cache. Restarting Explorer alone isn't always enough; the surviving workaround is to give the at-risk extension a **classic verb fallback** under `HKCU` so it lives in the registry independent of packaged-COM activation:

```
HKCU\Software\Classes\Directory\Background\shell\<Name>\(Default) = "Open in &Terminal"
HKCU\Software\Classes\Directory\Background\shell\<Name>\Icon      = "<exe>,0"
HKCU\Software\Classes\Directory\Background\shell\<Name>\NoWorkingDirectory = ""
HKCU\Software\Classes\Directory\Background\shell\<Name>\command\(Default) = "\"<exe>\" -d \"%V\""
```

…and the same under `Directory\shell\<Name>` (use `%1` instead of `%V` for the directory-as-item scope). For UWP-only apps (AMD Adrenaline), the command is `explorer.exe shell:AppsFolder\<PackageFamilyName>!<ApplicationId>`. These are user-scope and reversible with `reg delete`.

## How RCMM defends against the cascade

RCMM defends against this automatically. `PackagedShellExtScanner` parses each package's `AppxManifest.xml` to learn which `ItemType`s each CLSID is registered for and to derive an AUMID (`PackageFamilyName!ApplicationId`). `PackagedShellExt.IsBackgroundExtension` is true when the manifest binds the CLSID to `Directory\Background`. Before `MainViewModel.ApplyPending` writes any Background-scoped CLSID to `Shell Extensions\Blocked`, `CascadeProtectionService.PlanProtections` enumerates the OTHER Background packaged extensions and emits classic-verb fallbacks at `HKCU\Software\Classes\Directory\Background\shell\RcmmProtect_<clsid-without-braces>` whose `command` is `explorer.exe shell:AppsFolder\<AUMID>`. After every unhide, if no Background packaged CLSID remains in `Blocked`, `CascadeProtectionService.UninstallAll` sweeps the `RcmmProtect_` verbs back out. User-authored classic verbs (e.g. `OpenInTerminal`, `AMDSoftware`) are untouched because the sweep is namespace-scoped to the `RcmmProtect_` prefix.

Protection is scoped to `Directory\Background` only — not the folder-as-item `Directory` scope. An earlier iteration of `IsProtectableScope` accepted both, which produced a duplicate "Open in Terminal" / "AMD Software" row when the user right-clicked a *selected* folder: the cascade lives in the modern flyout (sourced from `Directory\Background`) and the folder-as-item scope was never affected, so the second protection verb was always extra noise. `MainViewModel.ApplyPending` also calls `CascadeProtectionService.PurgeStaleDirectoryScopeProtections()` on every Apply to scrub any duplicates that older builds wrote into `HKCU\Software\Classes\Directory\shell\RcmmProtect_*`.

## Pitfalls baked into the implementation after first-iteration mistakes

- **Skip protection when legacy menu hack is active.** The cascade only manifests in the modern flyout's `IExplorerCommand` enumeration. With the legacy hack on (HKCU `…\CLSID\{86ca1aa0-…}\InprocServer32` exists), the user never sees the modern menu, so the protection verbs would just clutter the classic menu with raw packaged-COM placeholders. `PlanProtections` early-returns when `IsLegacyMenuModeActive()` is true.
- **Use `PublisherDisplayName`, not `DisplayName`.** A packaged COM Server's `DisplayName` is the technical class name ("Catalyst Context Menu extension", "WindowsTerminalShellExt"); its `ApplicationDisplayName` (surfaced as `PackagedShellExt.PublisherDisplayName`) is the friendly app name ("AMD Software", "Terminal"). The protection verb's `(default)` is what Explorer renders — using the technical name produced exactly the ugly placeholders this feature was meant to avoid.
- **Don't fall through `LogoPath` → `DllPath` for Icon.** A WindowsApps DLL usually has zero icon resources, so writing it as `Icon` produces an iconless menu item. When `LogoPath` doesn't resolve, leave `Icon` unset and let Explorer fall back to a default.

Tests: `CascadeProtectionServiceTests`, `PackagedManifestParserTests`.
