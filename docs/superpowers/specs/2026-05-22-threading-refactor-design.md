# Threading refactor — move Rescan/Apply off the UI thread (C1 + H1)

- **Date:** 2026-05-22
- **Status:** Approved (design); pending implementation plan
- **Source:** `AUDIT.md` findings **C1** (Critical) and **H1** (High), plus **M-VM1** (Apply re-entrancy) folded in.

## Problem

`MainViewModel.Rescan()` runs the entire discovery pipeline — live `IContextMenu` COM capture, AppxManifest parsing, six-scope registry walks, and a COM probe of every CLSID — **synchronously on the UI thread**. It is invoked from the `MainWindow` constructor (`MainWindow.xaml.cs:74`) and again, after `ApplyPending()` + `ExplorerRestart().Restart()`, inside the non-async `FooterApply_Click` (`:126-132`). The window freezes on launch and on every Apply (**C1**).

Separately, `MainViewModel` mutates its bound `ObservableCollection`s (`AllEntries`, `PendingChangeIds`) and raises `PropertyChanged` (`ObservableObject.Raise/SetField`) with **no thread marshaling** (**H1**). This is currently "safe" only because everything runs on the UI thread — i.e. because of C1. Moving Rescan off-thread without addressing H1 would throw `RPC_E_WRONG_THREAD` inside WinUI.

## Goals

- Rescan and the Apply→restart→rescan sequence run off the UI thread; the window stays responsive.
- The Core ViewModel routes all UI-affecting mutations (bound collections + `PropertyChanged`) back to the UI thread through an injected abstraction (Core cannot reference `DispatcherQueue`).
- The Apply button cannot be re-entered while an operation is in flight (M-VM1).
- All 28 existing test files compile **and pass unchanged**.
- Strictly behavior-preserving: identical scan/apply results, no new UI.

## Non-goals

No loading spinner / "Scanning…" affordance. No fix for the other audit findings (M-VM2 async-void dispatcher lambdas, the `MainViewModel` god-class extraction, ExplorerRestart hardening, conditional restart). ExplorerRestart stays **unconditional** on Apply, matching today's behavior.

## Design

### 1. UI-dispatch abstraction (injected `Action<Action>`)

`MainViewModel` gains `private readonly Action<Action> _post;`, set from a new **optional last** constructor parameter:

```csharp
public MainViewModel(/* ...existing 15 params... */,
                     Action<Action>? postToUi = null)
{
    // ...
    _post = postToUi ?? (a => a());   // default: run inline (tests, headless)
}
```

- Tests construct with 7 / 9 positional args → the new optional param is absent → `_post` runs inline → synchronous, identical behavior.
- The composition root (`MainWindow.xaml.cs:53`) passes `a => DispatcherQueue.TryEnqueue(() => a())`.

Rejected alternatives: an `IUiDispatcher` interface (extra ceremony for one callback, against the project's lean grain); capturing `SynchronizationContext` (implicit, fragile, depends on the construction thread).

### 2. Rescan split

- The current `Rescan()` body becomes `private void RescanCore()`, wrapped in a top-level `try/catch` that logs via `Log.Error("rescan", …)` and swallows. (Today an exception in Rescan crashes the app/test; this is a strict improvement and is required for safe fire-and-forget.)
- The tail is reordered so the worker finishes touching `_allRows` before the UI reads it. Worker thread: `_pendingHide.Clear()`, `_pendingUnhide.Clear()`, `_knownStore.Save(...)`, dump logging. Then a **single** posted UI block:

  ```csharp
  _post(() =>
  {
      FilterIntoAllEntries();                       // mutates AllEntries
      PendingChangeIds.Clear();                     // bound collection
      Raise(nameof(RequiresExplorerRestart));       // PropertyChanged
      RescanComplete?.Invoke();                     // host loads icons
  });
  ```

- Public surface:
  - `public void Rescan() => RescanCore();` — synchronous; used by tests and as the core.
  - `public Task RescanAsync() => Task.Run(RescanCore);` — used by the app.

`FilterIntoAllEntries` thereby only ever mutates `AllEntries` on the UI thread (it is also called from the `ShowBuiltIns` setter, already on the UI thread).

### 3. Apply orchestration

- `ApplyPending()` stays synchronous (tests assert immediately after it). Its UI-touching tail is wrapped in `_post(...)`: `PendingChangeIds.Clear()`, `Raise(nameof(RequiresExplorerRestart))`, and the `_addPage.MarkClean()` call (which raises `HasPendingChanges`). Inline default → identical for tests.
- `MainWindow.FooterApply_Click` becomes `async` with a re-entrancy guard:

  ```csharp
  private bool _busy;
  private async void FooterApply_Click(object sender, RoutedEventArgs e)
  {
      if (_busy) return;
      _busy = true;
      FooterApplyButton.IsEnabled = false;
      try
      {
          await Task.Run(() => ViewModel.ApplyPending());
          await Task.Run(() => new ExplorerRestart().Restart());
          await ViewModel.RescanAsync();
      }
      catch (Exception ex) { Log.Error("apply", "apply/rescan failed", ex); }
      finally { _busy = false; UpdateFooterApply(); }
  }
  ```

### 4. Startup

`MainWindow` ctor `ViewModel.Rescan();` → `_ = ViewModel.RescanAsync();` (fire-and-forget; safe because `RescanCore` cannot fault). The window paints immediately; rows populate when the scan completes via the existing `RescanComplete` → icon-load path.

### 5. Error handling

`RescanCore`'s top-level try/catch makes both `Rescan()` and `RescanAsync()` non-throwing. `ApplyPending` keeps its existing per-id try/catch internally; the `FooterApply_Click` try/catch covers the orchestration.

## Files touched

| File | Change |
|---|---|
| `manager/src/RCMM.Core/ViewModels/MainViewModel.cs` | `_post` field + ctor param; `Rescan()`→`RescanCore()` split + `RescanAsync()`; wrap Rescan/Apply UI tails in `_post`; top-level try/catch in `RescanCore`. |
| `manager/src/RCMM/MainWindow.xaml.cs` | async `FooterApply_Click` + `_busy` guard; startup `RescanAsync` fire-and-forget; pass `postToUi` dispatcher in the composition root. |
| `manager/test/RCMM.Tests/MainViewModelDispatchTests.cs` (new) | Verifies the UI boundary (below). |

No XAML changes. No new dependencies (BCL + existing WinUI only).

## Testing

- **Regression:** all 28 existing test files compile and pass unchanged (synchronous default `_post`; `Rescan()`/`ApplyPending()` remain synchronous).
- **New unit test (TDD):** inject a `postToUi` that **defers** actions into a list instead of running them. After `Rescan()`, assert `AllEntries.Count == 0` (the UI block was deferred, not run); then drain the deferred list and assert `AllEntries` is populated. This deterministically proves the collection/`PropertyChanged` mutations are funneled through the dispatcher rather than executed inline on the worker.

## Risks & parity notes

- **Behavior parity:** the synchronous (default `_post`) path executes the same code in the same order as today, so test results are identical. The only reordering (`_knownStore.Save` before the posted UI block) is between operations with no observable interaction.
- **Thread safety of `_allRows`:** `RescanCore` finishes all writes to `_allRows` before the posted UI block reads it (via `FilterIntoAllEntries`) and before `_knownStore.Save` reads it; no concurrent writer exists. While a scan is in flight the Apply button is disabled, so a user toggle (`OnRowToggled`) can't race the worker.
- **Fire-and-forget startup:** safe because `RescanCore` swallows its own exceptions; the task cannot become unobserved-faulted.
