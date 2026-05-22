# C1+H1 Threading Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move `MainViewModel.Rescan()` and the Apply→restart→rescan sequence off the WinUI UI thread, marshalling the ViewModel's bound-collection / `PropertyChanged` mutations back to the UI thread through an injected dispatcher — without freezing the window and without changing any results.

**Architecture:** Inject an `Action<Action> postToUi` into `MainViewModel` (default = run inline). All UI-affecting mutations go through it. `Rescan()` stays synchronous (tests rely on it); a new `RescanAsync()` wraps it in `Task.Run`. The View calls `RescanAsync()` and runs Apply off-thread inside an `async` handler guarded against re-entrancy.

**Tech Stack:** C# / .NET 8, WinUI 3 (Windows App SDK), xUnit. No new dependencies.

**Branch:** `threading-refactor` (already created). Spec: `docs/superpowers/specs/2026-05-22-threading-refactor-design.md`.

**Test commands (PowerShell):**
- Full suite: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj`
- Single test class: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~MainViewModelDispatchTests"`
- App build: `dotnet build manager\src\RCMM\RCMM.csproj`

---

## Task 1: Marshal UI mutations through an injected dispatcher (H1)

**Files:**
- Test: `manager/test/RCMM.Tests/MainViewModelDispatchTests.cs` (create)
- Modify: `manager/src/RCMM.Core/ViewModels/MainViewModel.cs` (ctor + `Rescan` tail at `:403-417` + `ApplyPending` tail at `:1061,:1065-1068`)

- [ ] **Step 1: Write the failing test**

Create `manager/test/RCMM.Tests/MainViewModelDispatchTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;
using Xunit;

namespace RCMM.Tests;

public class MainViewModelDispatchTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly TargetProvider _targets;

    public MainViewModelDispatchTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"rcmm-disp-{Guid.NewGuid():N}");
        _targets = new TargetProvider(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // Mirrors MainViewModelTests.BuildSut + the single-row capture scenario,
    // but lets the caller supply postToUi.
    private MainViewModel BuildSut(Action<Action>? postToUi)
    {
        var reg = new FakeRegistry();
        var cap = new FakeContextMenuCaptureService();
        var mapper = new VerbToRegistryMapper(reg);
        var hide = new HideService(reg);
        var files = new FakeFileVersionReader();
        var resolver = new ClsidResolver(reg);
        var shellexIndex = new ShellexNameIndex(reg, resolver, files);

        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\git_shell", "", "Open Git Bash here");
        var target = _targets.GetTargets().First(p => p.EndsWith(".txt"));
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "Open Git Bash here", Verb = "git_shell" }
        };

        return new MainViewModel(cap, _targets, mapper, hide, reg, files, shellexIndex, postToUi: postToUi);
    }

    [Fact]
    public void Rescan_defers_collection_mutations_to_postToUi()
    {
        var deferred = new List<Action>();
        var vm = BuildSut(postToUi: deferred.Add);

        vm.Rescan();

        // The UI-affecting block (FilterIntoAllEntries etc.) was handed to
        // postToUi, not executed inline on the calling (worker) thread.
        Assert.Empty(vm.AllEntries);
        Assert.NotEmpty(deferred);

        // Draining the queue applies the mutations — the same result a
        // synchronous rescan produces.
        foreach (var action in deferred.ToList()) action();
        Assert.Single(vm.AllEntries);
        Assert.Equal("Open Git Bash here", vm.AllEntries[0].DisplayName);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~MainViewModelDispatchTests"`
Expected: **build error** — `MainViewModel` has no `postToUi` parameter.

- [ ] **Step 3: Add the `_post` field and constructor parameter**

In `MainViewModel.cs`, add the field next to the other readonly fields (after `:24`, the `_shellexInvoker` field):

```csharp
    // Posts an action to the UI thread. Defaults to inline execution so tests
    // and headless callers run synchronously; the app injects a DispatcherQueue
    // marshaller. All AllEntries / PendingChangeIds / PropertyChanged mutations
    // go through this so they never touch a bound collection off the UI thread.
    private readonly Action<Action> _post;
```

Change the constructor signature — add a new **last** optional parameter (after `cascadeProtector`):

```csharp
        CascadeProtectionService? cascadeProtector = null,
        Action<Action>? postToUi = null)
```

And set it at the end of the constructor body (after `_cascadeProtector = cascadeProtector;`):

```csharp
        _post = postToUi ?? (a => a());
```

- [ ] **Step 4: Route the Rescan tail through `_post`**

In `MainViewModel.cs`, replace the current end-of-`Rescan` block (lines `403-417`):

```csharp
        FilterIntoAllEntries();
        _pendingHide.Clear();
        _pendingUnhide.Clear();
        PendingChangeIds.Clear();
        Raise(nameof(RequiresExplorerRestart));
        RescanComplete?.Invoke();
        Log.Info("rescan", $"end rows={_allRows.Count} withHideTargets={rowsWithHide} builtIn={rowsBuiltIn} visible={AllEntries.Count}");
        // Persist the current snapshot so the next rescan can recover ghosts.
        _knownStore.Save(_allRows.ConvertAll(r => r.Entry));
        for (int i = 0; i < _allRows.Count; i++)
        {
            var r = _allRows[i];
            var src = string.IsNullOrEmpty(r.Entry.Source) ? "Unknown" : r.Entry.Source;
            Log.Debug("dump", $"#{i:D2} '{r.Entry.DisplayName}' src='{src}' sub={r.Entry.IsSubmenu} hideTargets={r.Entry.HideTargets.Count} icon='{r.Entry.IconPath ?? ""}'");
        }
```

with (worker-thread work first; UI-affecting work inside `_post`):

```csharp
        _pendingHide.Clear();
        _pendingUnhide.Clear();
        // Persist the current snapshot so the next rescan can recover ghosts.
        _knownStore.Save(_allRows.ConvertAll(r => r.Entry));
        for (int i = 0; i < _allRows.Count; i++)
        {
            var r = _allRows[i];
            var src = string.IsNullOrEmpty(r.Entry.Source) ? "Unknown" : r.Entry.Source;
            Log.Debug("dump", $"#{i:D2} '{r.Entry.DisplayName}' src='{src}' sub={r.Entry.IsSubmenu} hideTargets={r.Entry.HideTargets.Count} icon='{r.Entry.IconPath ?? ""}'");
        }
        // UI-affecting mutations must run on the UI thread. _post runs them
        // inline by default and marshals them to the dispatcher in the app.
        _post(() =>
        {
            FilterIntoAllEntries();
            PendingChangeIds.Clear();
            Raise(nameof(RequiresExplorerRestart));
            RescanComplete?.Invoke();
            Log.Info("rescan", $"end rows={_allRows.Count} withHideTargets={rowsWithHide} builtIn={rowsBuiltIn} visible={AllEntries.Count}");
        });
```

(`rowsWithHide` and `rowsBuiltIn` are locals declared earlier in the method; they are captured by the lambda closure.)

- [ ] **Step 5: Run the new test to verify it passes**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~MainViewModelDispatchTests"`
Expected: **PASS**.

- [ ] **Step 6: Route the ApplyPending tail through `_post`**

In `MainViewModel.cs`, the `_addPage.MarkClean();` call (line `1061`, inside the additions `try` block) raises `HasPendingChanges`. Wrap it:

```csharp
                _post(() => _addPage.MarkClean());
```

Then replace the `ApplyPending` end block (lines `1065-1068`):

```csharp
        _pendingHide.Clear();
        _pendingUnhide.Clear();
        PendingChangeIds.Clear();
        Raise(nameof(RequiresExplorerRestart));
```

with:

```csharp
        _pendingHide.Clear();
        _pendingUnhide.Clear();
        _post(() =>
        {
            PendingChangeIds.Clear();
            Raise(nameof(RequiresExplorerRestart));
        });
```

- [ ] **Step 7: Run the full suite to verify everything is green**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj`
Expected: **all tests pass** (the new dispatch test plus all pre-existing tests — the inline default keeps their behavior identical).

- [ ] **Step 8: Commit**

```bash
git add manager/test/RCMM.Tests/MainViewModelDispatchTests.cs manager/src/RCMM.Core/ViewModels/MainViewModel.cs
git commit -m "$(cat <<'EOF'
refactor: marshal MainViewModel UI mutations through injected dispatcher

Add an Action<Action> postToUi ctor param (defaults to inline) and route
AllEntries/PendingChangeIds/PropertyChanged mutations in Rescan and
ApplyPending through it. Addresses AUDIT.md H1. Behavior-preserving;
all existing tests stay green.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Run Rescan off the UI thread — `RescanAsync` + crash guard (C1 core)

**Files:**
- Test: `manager/test/RCMM.Tests/MainViewModelDispatchTests.cs` (add a test)
- Modify: `manager/src/RCMM.Core/ViewModels/MainViewModel.cs` (usings + split `Rescan` at `:147`)

- [ ] **Step 1: Write the failing test**

Add to `MainViewModelDispatchTests.cs` (inside the class). Note the file already has `using System.Linq;`; add `using System.Threading.Tasks;` to the top of the file.

```csharp
    [Fact]
    public async Task RescanAsync_runs_to_completion_and_populates_AllEntries()
    {
        var vm = BuildSut(postToUi: null); // inline default

        await vm.RescanAsync();

        Assert.Single(vm.AllEntries);
        Assert.Equal("Open Git Bash here", vm.AllEntries[0].DisplayName);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~MainViewModelDispatchTests"`
Expected: **build error** — `MainViewModel` has no `RescanAsync`.

- [ ] **Step 3: Add the `System.Threading.Tasks` using**

At the top of `MainViewModel.cs`, add to the using block:

```csharp
using System.Threading.Tasks;
```

- [ ] **Step 4: Split `Rescan` into a guarded public wrapper + private core + async entry point**

In `MainViewModel.cs`, change the method header at line `147` from:

```csharp
    public void Rescan()
    {
        Log.Info("rescan", "begin");
```

to:

```csharp
    public void Rescan()
    {
        try { RescanCore(); }
        catch (Exception ex) { Log.Error("rescan", "rescan failed", ex); }
    }

    /// <summary>Runs the rescan pipeline on a background thread. UI-affecting
    /// mutations are marshalled back via the injected postToUi dispatcher.</summary>
    public Task RescanAsync() => Task.Run(Rescan);

    private void RescanCore()
    {
        Log.Info("rescan", "begin");
```

(The rest of the original method body is unchanged — it now belongs to `RescanCore`. The try/catch lives in the public `Rescan`, so both `Rescan()` and `RescanAsync()` are non-throwing, making the fire-and-forget startup in Task 3 safe.)

- [ ] **Step 5: Run the new test and the full suite**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj`
Expected: **all tests pass**, including `RescanAsync_runs_to_completion_and_populates_AllEntries` and the existing `Rescan_defers_collection_mutations_to_postToUi` (still green: `Rescan()` → `RescanCore()` still defers the UI block).

- [ ] **Step 6: Commit**

```bash
git add manager/test/RCMM.Tests/MainViewModelDispatchTests.cs manager/src/RCMM.Core/ViewModels/MainViewModel.cs
git commit -m "$(cat <<'EOF'
refactor: add MainViewModel.RescanAsync and guard RescanCore

Split Rescan into a public try/catch wrapper, a private RescanCore body,
and RescanAsync (Task.Run). Lets the View run discovery off the UI thread
without faulting an unobserved task. Addresses AUDIT.md C1 (core).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Wire the View — async Apply, busy guard, async startup, inject dispatcher (C1 app + M-VM1)

**Files:**
- Modify: `manager/src/RCMM/MainWindow.xaml.cs` (composition root `:53`, startup `:74`, `FooterApply_Click` `:126-132`, add `_busy` field)

This task changes WinUI view code, which has no unit-test harness. Verification is a successful app build plus a manual smoke check.

- [ ] **Step 1: Add the `_busy` field**

In `MainWindow.xaml.cs`, next to the other private fields (after `:22`, the `_minSize` field):

```csharp
    private bool _busy;
```

- [ ] **Step 2: Inject the dispatcher in the composition root**

Replace the `ViewModel = new MainViewModel(...)` statement at line `53`:

```csharp
        ViewModel = new MainViewModel(capture, targets, mapper, hide, registry, files, shellexIndex, entryScanner, packagedScanner, commandStore, shellexKeyIndex, shellexInvoker, addPage, additionApplier, cascadeProtector);
```

with (append the `postToUi:` argument):

```csharp
        ViewModel = new MainViewModel(capture, targets, mapper, hide, registry, files, shellexIndex, entryScanner, packagedScanner, commandStore, shellexKeyIndex, shellexInvoker, addPage, additionApplier, cascadeProtector,
            postToUi: action => DispatcherQueue.TryEnqueue(() => action()));
```

- [ ] **Step 3: Make startup non-blocking**

Replace the startup call at line `74`:

```csharp
        ViewModel.Rescan();
```

with:

```csharp
        _ = ViewModel.RescanAsync();
```

- [ ] **Step 4: Make the Apply handler async with a re-entrancy guard**

Replace `FooterApply_Click` (lines `126-132`):

```csharp
    private void FooterApply_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ApplyPending();
        new ExplorerRestart().Restart();
        ViewModel.Rescan();
        UpdateFooterApply();
    }
```

with:

```csharp
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
        finally
        {
            _busy = false;
            UpdateFooterApply();
        }
    }
```

(`using System;`, `using System.Threading.Tasks;`, `using RCMM.Core.Diagnostics;` and `using RCMM.Core.Services;` are already present in this file.)

- [ ] **Step 5: Build the app**

Run: `dotnet build manager\src\RCMM\RCMM.csproj`
Expected: **Build succeeded**, 0 errors.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj`
Expected: **all tests pass**.

- [ ] **Step 7: Manual smoke check**

Launch the built app (or `dotnet run --project manager\src\RCMM\RCMM.csproj`) and confirm:
- The window appears immediately on launch (no multi-second freeze); the entry list fills in a moment later.
- Navigating to the Show/Hide → a scope list, toggling an entry, and clicking **Apply** does not freeze the window; the Apply button disables during the operation and re-enables when done.
- After Apply, Explorer restarts and the list re-populates.

- [ ] **Step 8: Commit**

```bash
git add manager/src/RCMM/MainWindow.xaml.cs
git commit -m "$(cat <<'EOF'
refactor: run rescan/apply off the UI thread in MainWindow

Startup now calls RescanAsync (fire-and-forget); FooterApply_Click is
async with a _busy re-entrancy guard and disables the Apply button while
working; the composition root injects a DispatcherQueue marshaller as
postToUi. Resolves AUDIT.md C1 and M-VM1.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-review

**Spec coverage:**
- Dispatch abstraction (injected `Action<Action>`, default inline) → Task 1 Step 3. ✔
- Rescan split (`RescanCore`/`Rescan`/`RescanAsync`, top-level try/catch, worker-then-`_post` tail ordering) → Task 1 Step 4 + Task 2 Step 4. ✔
- ApplyPending UI tail + `MarkClean` via `_post` → Task 1 Step 6. ✔
- Async `FooterApply_Click` + `_busy` guard + button disable (M-VM1) → Task 3 Step 4. ✔
- Fire-and-forget startup → Task 3 Step 3. ✔
- Composition-root dispatcher wiring → Task 3 Step 2. ✔
- Deferred-dispatch unit test → Task 1 Step 1; `RescanAsync` completion test → Task 2 Step 1. ✔
- All 28 existing tests stay green → Task 1 Step 7, Task 2 Step 5, Task 3 Step 6. ✔
- Non-goals (no spinner, no other findings) → respected; no task touches them. ✔

**Placeholder scan:** none — every code/edit step shows concrete code and an exact command with expected output.

**Type/name consistency:** `_post`, `postToUi`, `RescanCore`, `Rescan`, `RescanAsync`, `_busy`, `FooterApplyButton`, `DispatcherQueue.TryEnqueue`, `ExplorerRestart`, `Log.Error` are used consistently across tasks and match the existing code read from `MainViewModel.cs`, `MainWindow.xaml.cs`, and `MainViewModelTests.cs`.
