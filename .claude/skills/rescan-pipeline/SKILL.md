---
name: rescan-pipeline
description: The MainViewModel.Rescan() 9-step discovery / rename / merge / filter pipeline and the hide-target kinds. Use when modifying how entries are discovered, probed, merged, renamed, deduped, or filtered into the visible list (AllEntries), or when diagnosing why an entry is missing, duplicated, or mislabeled.
---

# Rescan pipeline

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

## Hide targets

Hide targets are one of:

- `LegacyDisable` value
- `HkcuMask` key (an HKCU shadow of an HKLM key)
- `BlockedShellExt` entry

Apply writes these via `HideService`; some kinds need an Explorer restart to take effect.

## Related

- Packaged-COM scanning, the legacy menu hack, and the `Directory\Background` cascade that step 2 / Apply must navigate live in the `windows-context-menu` skill.
- Changes to steps 5, 8, or 9 alter what the user sees — see the discovery-and-rename path-scoped rule before touching them.
