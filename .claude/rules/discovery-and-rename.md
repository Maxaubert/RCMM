---
paths:
  - "manager/src/RCMM.Core/ViewModels/**/*.cs"
  - "manager/src/RCMM.Core/Services/**/*.cs"
  - "manager/src/RCMM/Util/IconHelper.cs"
---

# Discovery, rename, and icon-resolution guardrails

You're editing the discovery / rename / filter / icon-resolution chain. These changes affect what the user actually sees in RCMM's list, and the "right thing" here is a product/UX decision, not just a code decision.

**Ask before assuming** when a change would:

- **Add an entry to the visible list or remove one** — i.e. change the step-9 filter in `MainViewModel` (`AllEntries`), the step-8 dedupe, or the suppressed/technical-name rules.
- **Touch the rename chain** — the step-5 pipeline: `IShellExtInit + IContextMenu` emitted names → `IExplorerCommand::GetTitle` → CommandStore verb-name derivation → the static override table (Defender, NVIDIA).
- **Touch icon resolution** — `IconHelper` and `MainViewModel.ResolveIconPath` (PNG bytes from `ExtractIconEx` / raw PNG / sibling-exe fallback).

Renaming noise to the real menu text (e.g. "Microsoft Security Client Shell Extension" → "Scan with Microsoft Defender") is intended behavior — preserve it; don't regress to raw technical names.

For the full step-by-step pipeline, see the `rescan-pipeline` skill. For why packaged-COM extensions, the legacy menu hack, and the `Directory\Background` cascade behave the way they do, see the `windows-context-menu` skill.

Conventions for this code: comments explain **why**, not what; long block comments on non-obvious pipeline decisions are welcome (see `MainViewModel.Rescan`, `ResolveIconPath`). No external NuGet dependencies beyond Windows App SDK / .NET BCL.
