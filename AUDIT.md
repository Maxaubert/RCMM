# RCMM code audit — 2026-05-22

Comprehensive, read-only audit of the RCMM codebase (71 source files, ~9,100 LOC, 28 test files). No code was changed.

**Method.** Five independent reviewers each audited one slice with clean context, returning severity-ranked findings; results were de-duplicated and merged here. Line numbers are as reported by the review. The two headline findings (**C1**, **H5**) were independently re-verified against the source.

**Severity scale.** `Critical` = crash on a common path / security / irreversible registry damage. `High` = real bug likely hit, or a security-relevant gap. `Medium` = robustness / maintainability. `Low` = minor / style.

## Executive summary

Overall health: **solid core, with one architectural defect and a cluster of input-handling gaps.** The COM/registry lifetime management, native-handle freeing, P/Invoke marshaling, and reversibility of hide/unhide are genuinely well done — the reviewers found *no* COM leaks, no double-releases, and no marshaling defects, which is rare for a codebase this interop-heavy. The real problems are concentrated in three places: (1) the entire discovery pipeline runs on the UI thread; (2) the user-authored "Add to RCM" command path has no quoting or input validation; (3) two of the most logic-dense pure functions (icon-spec / file-version path parsing) are private and completely untested.

| Severity | Count |
|---|---|
| Critical | 1 |
| High | 9 |
| Medium | 16 |
| Low | 22 |

Recommended remediation order: **C1 → H1** (threading; they're coupled), then **H5 + H6** (Add-to-RCM safety), then **H3 + H4** (registry safety), then **H8** (test the untested parsers), then the rest.

---

## Remediation log — 2026-07 follow-up audit (shipped 0.7.5 → 0.7.8)

A second, adversarially-verified audit (finders per subsystem → 3-lens refutation → coverage critic) confirmed a further set of defects, several new and two more severe than this document's originals. All of the below are fixed on `main`, each via its own issue → branch → PR with a test proven to fail without the fix.

| Area | Defect | Fixed in | Relates to |
|---|---|---|---|
| Add-to-RCM (elevated) | Folder name injected into the elevated `rcmm-action.ps1` `adminterm` `-Command` string ran arbitrary code as admin | PR #12 / #13 | H5 |
| Add-to-RCM (template) | `PowerShell here` embedded `%V` in a `-Command` string; `$(…)` in a folder name executed. `%V` is Explorer-substituted so RCMM can't escape it — now hosted via `wt -d "%V" powershell` | PR #15 / #14 | H5 |
| Add-to-RCM (data loss) | `DeleteFolder` reparented child entries but not subfolders, so `AdditionApplier` silently dropped the whole subtree on Apply | PR #12 / #13 | — |
| Add-to-RCM (data loss) | Hiding an RCMM-added entry was silently reverted by `PurgeOwnedKeys`; hidden state now persists in `AdditionEntry.Hidden` (schema v4) | PR #11 | — |
| Hide / apply (data loss) | `HkcuMask` hide/unhide destroyed a per-user (HKCU) shellex's real registration; now stashes + restores, never `DeleteKey`s a live key | PR #19 / #18 | more severe than M-D1 / M-H2 |
| ViewModels (race) | Row toggles during a background Apply/Rescan corrupted the pending-hide dictionaries; now lock-guarded + snapshot-at-boundary | PR #17 / #16 | distinct from C1 / H1 (already fixed) |
| ViewModels (race) | Startup `RescanAsync` could race the template-update dialog's `ApplyPending`; a `_workGate` now serializes them | PR #21 / #20 | — |
| Persistence | v2→v3 migration stamped entries as template-derived by Name alone, so a hand-authored entry could later be offered a destructive "update"; now requires a structural match | PR #21 / #20 | — |

**Still open (hand-confirmed, not yet fixed):** a hidden shellex can become un-unhideable when its rename fails on a later rescan (`FilterIntoAllEntries` runs `LooksTechnical` before the `IsHidden` hatch) — issue #22; and a File-scope entry leaks its per-extension verb when an extension is dropped (`PurgeOwnedKeys` only visits extensions still in state) — issue #23.

**Coverage gap:** the follow-up audit did not probe the WinUI view layer (drag-reorder, dialogs, converters) or `IconRender.cs`; those remain unexamined by either audit.

---

## Critical

### C1 — The whole rescan/apply pipeline runs synchronously on the UI thread
`manager/src/RCMM/MainWindow.xaml.cs:74` (startup) and `:130` (`FooterApply_Click`). *(verified)*
`Rescan()` performs live `IContextMenu` COM capture against ~8 sample targets, an AppxManifest XML parse per package, six-scope registry walks, and a COM probe of every CLSID — all blocking. It's invoked synchronously from the window constructor and again inside the non-async `FooterApply_Click` (which also kills+relaunches Explorer first). On a machine with many shell extensions this freezes the window for seconds on launch and on every Apply.
- **Why it matters:** primary user-visible defect; the app appears hung exactly when it's doing its main job.
- **Fix:** make `Rescan` return `Task`, run the body on a background thread (`await Task.Run(...)`), and marshal only the `ObservableCollection`/`PropertyChanged` mutations back to the dispatcher. Make `FooterApply_Click` async and disable the Apply button for the duration (also closes the re-entrancy gap, M-VM1). **Do this together with H1.**

---

## High

### H1 — ViewModel has no dispatcher affinity for its bound collections
`manager/src/RCMM.Core/ViewModels/MainViewModel.cs:404-467` (`FilterIntoAllEntries`, `PendingChangeIds.Clear`, `Raise`).
`AllEntries` is an `ObservableCollection` bound to the UI, mutated with no thread enforcement. It's "safe" today *only* because everything runs on the UI thread — i.e. because of C1. The moment Rescan moves off-thread (the C1 fix), mutating the collection off the dispatcher throws `RPC_E_WRONG_THREAD` inside WinUI.
- **Fix:** inject a UI-post abstraction into the Core VM (`Action<Action> postToUi`, since Core can't reference `DispatcherQueue`) and route all bound-collection / `PropertyChanged` mutations through it.

### H2 — Leaked managed `Icon` wrapper in icon extraction
`manager/src/RCMM/Util/IconHelper.cs:190` and `:193`.
`Icon.FromHandle(h).Clone()` — `Clone()` returns the kept icon, but the intermediate `Icon.FromHandle(...)` wrapper is never disposed. The underlying HICON is freed by `DestroyIcon(h)` in the `finally`, but the managed wrapper is leaked to the finalizer queue, repeatedly, for every extracted icon on every rescan.
- **Fix:** `using var tmp = Icon.FromHandle(h); return (Icon)tmp.Clone();` before `DestroyIcon`.

### H3 — Cascade protection can be swept away while still needed
`manager/src/RCMM.Core/Services/CascadeProtectionService.cs` (`UninstallAll`) driven by `MainViewModel.cs:1022-1036`.
The sweep removes all `RcmmProtect_` verbs whenever no Background CLSID remains in HKCU `Blocked` *according to this session's `_backgroundExtsByClsid`*. If a Background packaged extension was hidden in a prior session but isn't enumerated this session (package updated/CLSID changed, or its manifest failed to parse so `IsBackgroundExtension` is false), the "still needed?" check returns false and the protections another still-blocked extension depends on are stripped — silently re-exposing it to the very cascade this feature defends against.
- **Fix:** decide "still needed" from the live contents of `Shell Extensions\Blocked` (enumerate the Blocked value names against a persisted manifest cache), not from this session's scan. At minimum, log the Blocked list before sweeping.

### H4 — Single-entry hide overloads write to HKCR (can land in HKLM)
`manager/src/RCMM.Core/Services/HideService.cs:30,44`.
The `Hide/Unhide(ContextMenuEntry)` overloads write `LegacyDisable` to `RegistryHive.ClassesRoot`. A write to `HKCR\<scope>\shell\<verb>` lands in HKLM when the source key is machine-wide — requiring admin and clobbering a key for all users, violating the HKCU-only safety model. Currently these overloads are **test-only / dead** (production uses the `HideTarget` list path), so live blast radius is zero — but they're `public` and a future caller could wire them up.
- **Fix:** delete the two `ContextMenuEntry` overloads (production doesn't use them), or redirect their ShellVerb branch to `HKCU\Software\Classes\…` like `VerbToRegistryMapper`. At minimum mark `[Obsolete]` with a "must not write HKCR" note.

### H5 — `cmd /k` + unescaped user command (injection surface)
`manager/src/RCMM.Core/Services/AdditionApplier.cs:72`. *(verified)*
`WrapForRunMode(VisibleTerminal, command)` is `"cmd /k " + command` with no quoting. The result becomes a registry verb `command`. The user authors their own command (so self-injection is low-stakes), but the real hazard is **path substitution**: Explorer expands `%V`/`%1` (the right-clicked folder/file path) into the line *before* cmd parses it, so a folder named with cmd metacharacters (`& calc &`, embedded quotes) can break out when the user's command references `%V`/`%1` unquoted. The bundled templates quote correctly; hand-written commands won't.
- **Fix:** prefer `cmd /s /k "<command>"` quoting the command as a unit; surface an editor warning when `%V`/`%1` appears unquoted; document loudly that the user owns the command line.

### H6 — Zero input validation in the Add-to-RCM editor
`manager/src/RCMM/Views/AddPage.xaml.cs:489-509` (create/edit path).
No validation of entry/folder `Name` or `Command`: empty, whitespace-only, oversized, or duplicate names flow straight to the registry verb's display value and command. Result: blank or broken menu items, silently. (Key *names* are GUIDs — `Guid.NewGuid().ToString("N")` — so there is **no** key-path injection, which is good; this is purely a value-quality gap.)
- **Fix:** in `SaveCurrent`/before Apply, trim names, reject empty `Name`/`Command`, cap length, surface inline errors, and block Apply when any entry is invalid.

### H7 — Non-CLSID strings can be emitted as a row's `Clsid`
`manager/src/RCMM.Core/Services/ClassicShellexScanner.cs:39-40`.
When neither the HKCR nor HKLM default value is a CLSID, `clsid` falls through to `defaultVal ?? name`, so a ProgID or arbitrary key name gets stored as the entry's `Clsid` (line ~65). The stricter sibling scanners (`ShellexNameIndex.cs:108-110`, `ShellexInvoker.cs:279-281`) correctly set `clsid = null` and skip. Downstream hide logic / `OwnerClsid` consumers may then treat a non-CLSID as a real CLSID.
- **Fix:** mirror the strict logic — `LooksLikeClsid(defaultVal) ? defaultVal : LooksLikeClsid(name) ? name : null` and null/skip otherwise.

### H8 — The two densest string parsers are private and untested
`manager/src/RCMM.Core/Services/Win32FileVersionReader.cs:26-50` (`NormalizePath`) and `manager/src/RCMM/Util/IconHelper.cs:105-169` (`ParseIconSpec`).
Both are branchy heuristics (strip quotes, split `path,index`, expand env vars, resolve bare name against System32) that feed the **guarded** icon-resolution / `IsBuiltIn` chain. Both are `private static` — not even reachable from tests. Regressions surface as wrong/missing icons, never as a failing test.
- **Fix:** make them `internal` + `[InternalsVisibleTo("RCMM.Tests")]` (or extract a shared `IconSpec` helper — see X3) and add table tests: `imageres.dll,-5302`, `"C:\path with space\vlc.exe",0`, `@shell32.dll,-1`, bare `notepad.exe %1`.

### H9 — Dedupe can drop the only loaded icon in a group
`manager/src/RCMM.Core/ViewModels/MainViewModel.cs:626-630` (`DeduplicateRowsByDisplayName`).
`PickDedupWinner` prefers the `verb:` row, and the rebuilt entry takes only the winner's icon. If a *dropped* sibling (e.g. the packaged row) was the one carrying the resolved icon and the winner has none, the icon is lost.
- **Fix:** when the winner's `IconPath`/`IconBytes`/`Icon` are empty, fall back to a dropped sibling's.

---

## Medium

### Discovery / scanners
- **M-D1** `ClassicShellexScanner.cs:36-37` — hide-recovery fallback reads only `HKLM\Software\Classes`; a shellex registered only under `HKCU\Software\Classes` (per-user install) vanishes from the list after hide. *Fix: add an HKCU `Software\Classes` fallback.*
- **M-D2** `ContextMenuCaptureService.cs:49-53` — on the 30 s STA timeout the worker thread is abandoned but the shared `List<CapturedItem>` it's still mutating is returned to the caller, which immediately enumerates it → `InvalidOperationException`/torn state, exactly in the misbehaving-handler case. *Fix: have the worker fill a local list and publish atomically only on clean completion.*
- **M-D3** `ShellexInvoker.cs:157` — same abandoned-worker race on `_emittedByClsid`/`_iconByClsid`/`_titleByClsid` after `done.Wait(45s)` times out. *Fix: same pattern.*
- **M-D4** `PackagedShellExtScanner.cs:120,268` — manifest/logo caches keyed on `installFolder` while the value depends on `packageFullName`; safe today (1:1) but a latent footgun. *Fix: key on the pair or assert the invariant.*

### Hide / apply / cascade
- **M-H1** `CascadeProtectionService.cs:122-135` — `Install` isn't atomic; a throw between the verb key and its `\command` subkey leaves a half-written verb that's *sticky* (idempotency check sees the parent key and skips it). *Fix: write `\command` first or have the idempotency check verify `\command\(default)` exists.*
- **M-H2** `Win32Registry.cs:20-23` — `DeleteKey` uses recursive `DeleteSubKeyTree`; for `HkcuMask` unhide this can delete unrelated values/subkeys a user manually placed under the masked key. *Fix: for HkcuMask removal, delete the single value/empty key; reserve the subtree delete for the protection verbs.*
- **M-H3** `ExplorerRestart.cs:9-17` — kills all `explorer.exe` and starts one with no verification/error handling; can leave the user with no shell, or spawn a stray Explorer window. *Fix: rely on Windows' auto-restart, poll to confirm a shell returned, wrap `Process.Start` in try/catch + log.*
- **M-H4** `ConfigStore.cs:42-52` — `ScheduleSave` mutates `_debounceCts` without synchronization; concurrent callers race. Low stakes (config only). *Fix: lock or `Interlocked.Exchange` the CTS swap.*
- **M-H5** `VerbToRegistryMapper.cs` *(uncertain)* — confirm hide targets are computed once at scan/hide time and replayed verbatim for unhide (appears so via `_pendingUnhide[id] = row.Entry.HideTargets`). If mappers are ever re-run for the inverse op, a scope that became invisible post-hide could be dropped, orphaning a marker. *Fix: add an explicit comment that mappers must not be re-run for the inverse.*

### Add-to-RCM
- **M-A1** `AdditionApplier.cs:211-259` — `command`/`Icon` written as `REG_SZ`; values containing `%ENV%` tokens won't expand. *Fix: write `REG_EXPAND_SZ` when the value contains `%...%` (needs an `IRegistry.SetValue` kind param).*
- **M-A2** `BinaryResolver.cs:47` — resolves a bare binary name against PATH order with no preference for System32; a writable earlier-PATH entry (classic hijack) gets baked into both the menu command and the Icon path. User-scope, so moderate. *Fix: resolve known system binaries against `Environment.SystemDirectory` first.*
- **M-A3** `AddPage.xaml.cs` (~1363 lines) — substantial non-UI logic in code-behind: `RecordsEffectivelyEqual`, drop-target bucket inference, and a duplicate `IsDescendant` (`:617-627`, also in `AddPageViewModel.cs:165-176`). *Fix: move pure logic into the testable VM.*
- **M-A4** `AdditionApplier` / model — dead `WorkingDir` field: the editor shows a "Working dir" box (default `%V`) but the applier never writes it, so it silently has no effect. *Fix: honor it or remove the box.*
- **M-A5** `AdditionStore.cs:80` — temp-write-then-`File.Replace` is atomic for the swap but can leave a stale `.tmp`; low risk. *Fix: delete a pre-existing `.tmp` first (optional).*

### ViewModels / UI
- **M-VM1** `MainWindow.xaml.cs:126-132` — `FooterApply_Click` is sync, blocking, with no button-disable, so a double-click re-enters Apply+restart+rescan. *Fix: folded into the C1/H1 async refactor.*
- **M-VM2** `MainWindow.xaml.cs:142,164` — `DispatcherQueue.TryEnqueue(async () => …)` is async-void on the dispatcher; tolerable because fully try-wrapped, but fragile. *Fix: prefer a named async method.*
- **M-VM3** `MainViewModel.cs:470-473` — malformed XML-doc (stray `<summary>` fragment from `DeduplicateRowsByDisplayName` leaked above `RestoreGhostEntries`). *Fix: delete the stray lines.*
- **M-VM4** `MainViewModel.cs` (1213 lines) — god-class mixing orchestration with stateless helpers (`ResolveIconPath`, `ResolveSourceAndBuiltIn`, `LooksTechnical`, `HkcrPathFor`, …). *Fix: extract the pure helpers into injected, testable services.* (See X1.)
- **M-VM5** `MainWindow.xaml.cs:28-53` — composition root (manual DI of ~17 services) lives in the window ctor. *Fix: move to a small bootstrapper/factory.*
- **M-VM6** `MainWindow.xaml.cs:64-72` — VM/AddPage event subscriptions never unsubscribed; harmless for a single long-lived window but fragile. *Fix: document the lifetime assumption.*

### Interop / cross-cutting
- **M-I1** `ShellInterop.cs` — `CoCreateInstance`/`CoInitializeEx`/`CoTaskMemFree`/`SHBindToParent`/etc. omit `ExactSpelling = true` (the shell32 imports set it); cosmetic/perf, not a correctness bug. *Fix: add it for consistency.*
- **M-I2** `ShellInterop.cs:70-81` — `IShellFolder.ParseDisplayName`/`SetNameOf` string params lack `[MarshalAs(LPWStr)]`; latent (methods never called). *Fix: annotate, or delete the unused methods.*
- **M-I3** `Log.cs:71` — `File.AppendAllText` opens+closes the file on every line; under verbose Debug logging that's heavy churn. *Fix: keep a `StreamWriter` open under the gate, or raise default `MinLevel` to `Info` in Release.*
- **M-T1** *(tests)* `FakeRegistry` doesn't model HKCR as the merged HKLM+HKCU view; `VerbToRegistryMapperTests` works around it by hand-faking the merge, so the HKCU-shadow behavior is validated against a fake, not real semantics. *Fix: synthesize the merged view for `ClassesRoot` reads, or document the limitation prominently.*
- **M-T2** *(tests)* Machine-dependent tests silently no-op/skip: `ApplyEndToEndTests.cs:122` (`if (winrar == null) return;`) and `ContextMenuCaptureServiceTests` (`[Fact(Skip=…)]`) — green can mean "skipped". Also `AdditionApplierIntegrationTests` writes live HKCU **without** the `[Trait("Integration")]` filter the other integration tests carry. *Fix: use `Assert.Skip`/`Skip.If` so skips are visible; add the missing trait.*

---

## Low

Grouped; all are minor robustness/style/cleanup.

- **Discovery:** `ShellexInvoker` CLSID lookup normalization is inconsistent (keys stored `Trim().ToUpperInvariant()`, `Lookup*` queries raw — absorbed by `OrdinalIgnoreCase` dicts but whitespace/brace differences would miss); case-sensitive `Path.GetExtension(t) == ".txt"` compare (`ShellexInvoker.cs:218`); `CommandStoreVerbIndex.cs:64` lists `CanonicalName` in `HandlerValueNames` though it's never a CLSID; `TargetProvider.Cleanup` silent `catch{}` leaves temp sample files; `PackagedShellExtScanner.cs:182` AUMID picks the first `<Application>` (multi-app packages).
- **Hide/apply:** `HideService` list overloads have no per-target try/catch (one failure strands sibling targets of the same entry); `VerbToRegistryMapper.cs:46` builds paths from scanned names without rejecting `\`; `CascadeProtectionService` `StripBraces`/`BuildCommand` don't validate CLSID/AUMID shape before embedding in a key name/command; `ExplorerRestart.cs:11` `catch{}` no log; `ConfigStore.LoadAsync` catches only `JsonException` (IO/permission errors propagate); `ConfigStore` leaves an orphan `.tmp` if serialize throws.
- **Add-to-RCM:** `AdditionApplier.cs:249-261` redundant scope-equality loop (dead branching); orphan-entry ordinals can share `001` with a real verb (ordering only, GUID keys differ); `AddPage` three overlapping persistence paths gated by `_suppressFieldChange` are brittle; dead `DropTop/DropBottom/DropIntoFill` Rectangles in the ItemTemplate; `SaveCurrent`→`MoveEntry` early-returns so a folder change via dropdown doesn't reposition in-editor *(uncertain; registry output still correct)*.
- **ViewModels/UI:** dead `HookThemeChange`/`UpdateForCurrentTheme`/`_uiSettings` (contradicts dark-only); `ScopeListViewModel` appears unused *(uncertain — confirm no XAML ref)*; duplicated XML-doc block (`:470` & `:582`); `EntryRowViewModel.HiddenChanged` is a public mutable field, not an `event`; broad `catch { return null; }` in `IconHelper`/icon parse swallows silently (log at Debug); `RestoreGhostEntries` `coveredClsids` seeding edge case can double-restore a ghost sharing a handler CLSID.
- **Interop/models:** `MSG.pt` flattened (fine, undocumented); `GCS_*A` constants dead-ish; `Config` is a mutable non-`sealed` class (convention drift vs records); `byte[] IconBytes` on `record` types breaks value-equality (latent if `==`/`Distinct()` ever used); `HideTarget` (Models) references `RegistryHive` (Services) — layering inversion.

---

## Cross-cutting themes

- **X1 — `MainViewModel` god-class (1213 lines).** The orchestrator carries icon-path resolution, source/built-in classification, technical-name heuristics, ghost restore, dedupe, and registry-path mapping. Extracting the stateless helpers into injected services (`IconPathResolver`, `SourceClassifier`, `RegistryPathUtil`) shrinks it toward orchestration-only and makes the heuristics testable (also unblocks H8).
- **X2 — Threading model (C1 + H1 + M-VM1/2).** A single coordinated async refactor of `Rescan`/Apply with an injected UI-post abstraction resolves the Critical, the dispatcher-affinity High, and two Mediums at once.
- **X3 — Triplicated path/string logic.** `Win32FileVersionReader.NormalizePath`, `IconHelper.ParseIconSpec`/`ResolveBareFilename`, and the MainViewModel path helpers each independently strip quotes / split `path,index` / resolve bare names against System32. `StripAccelerator` is also duplicated verbatim in `ContextMenuCaptureService.cs:452` and `ShellexInvoker.cs:405`. Consolidate into shared, tested helpers (resolves H8 + several Lows).
- **X4 — Possibly-dead legacy path.** `Config`/`ContextMenuEntry`-via-`ConfigStore` looks superseded by `AdditionState`/`MenuEntry`/`KnownEntriesStore`. Confirm and remove if dead. *(uncertain)*

---

## Test coverage

| Area | Tested? | Risk if untested |
|---|---|---|
| `HideService` (all 3 hide kinds, both directions, restart rules) | ✅ yes | low |
| `VerbToRegistryMapper` | ✅ yes | low (but against a non-merged `FakeRegistry` — M-T1) |
| `CascadeProtectionService` (plan/install/uninstall/purge/legacy-hack/idempotence) | ✅ yes (10) | low |
| `PackagedManifestParser` | ✅ yes | low |
| `AdditionApplier` (scopes, wrapping, ordinals, folders, purge, idempotence) | ✅ thorough | low |
| Classic verb / shellex / entry scanners, CommandStore, ClsidResolver, stores | ✅ yes | low |
| `MainViewModel` rescan/dedupe/toggle/apply | ⚠️ partial | medium — no packaged-COM cascade path, no rename-pipeline coverage |
| `Win32MuiStringResolver` (real P/Invoke + non-`@` passthrough) | ❌ no (fake only) | medium |
| **`Win32FileVersionReader.NormalizePath`** | ❌ no | **high** (H8) |
| **`IconHelper.ParseIconSpec`** | ❌ no | **high** (H8) |
| `Log` rotation / thread-safety | ❌ no | medium |
| `ShellInterop` / capture / invoker COM marshaling | ⚠️ skipped (needs STA) | medium |
| Add-to-RCM command quoting / injection (H5) | ⚠️ partial | medium — wrap string tested, not metachar/space safety |

---

## What was verified correct (don't re-investigate)

The reviewers explicitly checked and **cleared** these high-risk areas:

- **COM lifetime** in the discovery slice: every `GetObjectForIUnknown` RCW balanced with `ReleaseComObject`; every `out IntPtr` balanced with `Marshal.Release`; PIDLs `ILFree`d; popup menus `DestroyMenu`d; `GetIcon`/`GetTitle` CoTaskMem buffers freed. No leaks, no double-releases. STA + `CoInitialize/CoUninitialize` pairing (including the `S_FALSE` case) correct.
- **P/Invoke marshaling:** signatures, x64 struct packing (`MENUITEMINFOW`, `MSG`), `bool`/HRESULT marshaling, `UINT_PTR`→`IntPtr` all correct. No memory-corruption defects.
- **Production hide path** writes only `HKCU\Software\Classes\…` (never HKLM); every `HideKind` has a correct inverse; the `RcmmProtect_` sweep is prefix-scoped so it cannot delete user-authored verbs.
- **GDI+ disposal** in `IconMaterializer`/`SvgPathParser` (`using` throughout); `IconHelper` does file/GDI work off the UI thread and marshals only `BitmapImage` construction back.
- **`Log`** never throws into callers (all writes serialized on a gate, all exceptions swallowed); **`AdditionApplier` Apply** is idempotent and cleanly reversible (purge-then-rewrite, GUID key names so no key-path injection); **`ScopePage`** subscribes/unsubscribes `RescanComplete` symmetrically.

## Strengths

Disciplined interop and registry safety; exceptionally well-commented pipeline intent (the why-comments the conventions ask for are actually present); strong, assertion-rich test suites for the safety-critical services (hide reversibility, cascade protection, addition applier) with small faithful fakes and consistent `Method_condition_expected` naming; and strong adherence to the stated conventions (file-scoped namespaces, `sealed`, records, no external NuGet) outside the few drift spots noted in Low.
