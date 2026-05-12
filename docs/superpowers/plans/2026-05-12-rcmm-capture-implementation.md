# RCMM Plan 2 — Capture-Based Classic Menu Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the registry-scan source of truth in RCMM with live `IContextMenu` captures across ~10 representative targets, so the list shown to the user mirrors exactly what Windows would render in the classic right-click menu. Hide actions still go through the registry via the existing `HideService`, now driven by a new `VerbToRegistryMapper` that fans out across all scopes a verb/CLSID is registered under.

**Architecture:** A new `ContextMenuCaptureService` runs on a dedicated STA worker thread, invokes `SHCreateItemFromParsingName` + `IContextMenu::QueryContextMenu` per target, and walks the resulting `HMENU`. The deduped capture feeds a `VerbToRegistryMapper` that produces zero or more `HideTarget`s per item using the existing Plan 1 scanners as a registry index. `MainViewModel.AllEntries` becomes the merged user-facing list.

**Tech Stack:** .NET 8, WinUI 3, C# 12, xUnit, COM interop (shell32.dll, user32.dll), existing `IRegistry`/`HideService`/`IconHelper` from Plan 1.

---

## File Structure

```
RCMM.Core/
  Interop/
    ShellInterop.cs                # P/Invoke + COM type defs
  Models/
    HideKind.cs                    # enum
    HideTarget.cs                  # record
    MenuEntry.cs                   # the user-facing row's data
    CapturedItem.cs                # one captured menu item from one target
  Services/
    IContextMenuCaptureService.cs  # interface
    ContextMenuCaptureService.cs   # real STA-threaded Win32 impl
    TargetProvider.cs              # ~10 representative paths + temp lifecycle
    VerbToRegistryMapper.cs        # turns captured items into HideTargets
    HideService.cs                 # MODIFIED — also accepts HideTarget list
  ViewModels/
    EntryRowViewModel.cs           # MODIFIED — wraps MenuEntry, not ContextMenuEntry
    MainViewModel.cs               # REWRITTEN — drives capture + map

RCMM.Tests/
  FakeContextMenuCaptureService.cs
  VerbToRegistryMapperTests.cs
  HideServiceTests.cs              # extended — keeps Plan 1 tests, adds HideTarget list path
  MainViewModelTests.cs            # REWRITTEN
  ContextMenuCaptureServiceTests.cs   # real-COM smoke test, [Trait("Integration","true")]

RCMM/ (WinUI app)
  MainWindow.xaml.cs               # MODIFIED — wires new services
  Views/ScopePage.xaml             # MODIFIED — toggle IsEnabled bound to CanHide
  Views/ScopePage.xaml.cs          # MODIFIED — uses Image.Source from IconBytes
```

Files removed:
- `RCMM.Core/Util/EntryFilters.cs` + `RCMM.Tests/EntryFiltersTests.cs` — capture is already filtered.
- Eventually: `Views/LandingPage.xaml(.cs)` (unused since Plan 1 wire-up; can stay or be deleted, plan keeps it for now to avoid noise).

Files kept verbatim and reused as registry index:
- `Models/Scope.cs`, `Models/EntryKind.cs`, `Models/ContextMenuEntry.cs`, `Models/Config.cs`, `Models/PendingChange.cs`
- `Services/IRegistry.cs`, `Win32Registry.cs`, `ConfigStore.cs`, `ClassicVerbScanner.cs`, `ClassicShellexScanner.cs`, `EntryScanner.cs`, `ClsidResolver.cs`, `Win32MuiStringResolver.cs`, `Win32FileVersionReader.cs`, `ExplorerRestart.cs`
- `Util/Win32.cs`, `Util/WindowMinSize.cs`, `Util/IconHelper.cs`
- `InvertBoolConverter.cs`, `ObjectToImageSourceConverter.cs`

---

## Conventions

- TDD where the unit has a clean seam. COM interop has no clean seam — tested only by an integration smoke test that requires Windows + dev machine, gated by `[Trait("Integration","true")]`.
- Every implementation task commits its own diff. Commit messages follow the existing style (`area: imperative summary`).
- Working dir: `C:\Users\Admin\Documents\Claude\Github\RCMM`. Build: `dotnet build manager/RCMM.sln`. Tests: `dotnet test manager/test/RCMM.Tests`.
- All registry access goes through `IRegistry`. All COM goes through `IContextMenuCaptureService`. Tests use `FakeRegistry` + `FakeContextMenuCaptureService`.

---

### Task 1: Models — HideKind, HideTarget, MenuEntry, CapturedItem

**Files:**
- Create: `manager/src/RCMM.Core/Models/HideKind.cs`
- Create: `manager/src/RCMM.Core/Models/HideTarget.cs`
- Create: `manager/src/RCMM.Core/Models/MenuEntry.cs`
- Create: `manager/src/RCMM.Core/Models/CapturedItem.cs`

- [ ] **Step 1: Create `HideKind.cs`**

```csharp
namespace RCMM.Core.Models;

public enum HideKind
{
    LegacyDisable,
    HkcuMask
}
```

- [ ] **Step 2: Create `HideTarget.cs`**

```csharp
using RCMM.Core.Services;

namespace RCMM.Core.Models;

public sealed record HideTarget(HideKind Kind, RegistryHive Hive, string Path, string? ValueName);
```

- [ ] **Step 3: Create `CapturedItem.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace RCMM.Core.Models;

public sealed record CapturedItem
{
    public required string TargetPath { get; init; }
    public required int Position { get; init; }
    public required string DisplayName { get; init; }
    public string? Verb { get; init; }
    public string? OwnerClsid { get; init; }
    public byte[]? IconBytes { get; init; }
    public bool IsSeparator { get; init; }
    public bool IsSubmenu { get; init; }
    public IReadOnlyList<CapturedItem> Children { get; init; } = Array.Empty<CapturedItem>();
}
```

- [ ] **Step 4: Create `MenuEntry.cs`**

```csharp
using System.Collections.Generic;

namespace RCMM.Core.Models;

public sealed record MenuEntry
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Source { get; init; }
    public byte[]? IconBytes { get; init; }
    public string? IconPath { get; init; }                // fallback when IconBytes is null
    public required IReadOnlyList<HideTarget> HideTargets { get; init; }
    public bool IsBuiltIn { get; init; }
    public bool IsHidden { get; init; }
    public bool IsSubmenu { get; init; }

    public bool CanHide => HideTargets.Count > 0;
}
```

- [ ] **Step 5: Build**

```powershell
dotnet build manager/RCMM.sln
```

Expected: 0 errors.

- [ ] **Step 6: Commit**

```powershell
git add manager
git commit -m "models: HideKind, HideTarget, MenuEntry, CapturedItem"
```

---

### Task 2: Extend HideService to apply HideTarget lists

**Files:**
- Modify: `manager/src/RCMM.Core/Services/HideService.cs`
- Modify: `manager/test/RCMM.Tests/HideServiceTests.cs`

The existing `Hide(ContextMenuEntry)` / `Unhide(ContextMenuEntry)` overloads stay (Plan 1's tests still pass). We add new `Hide(IReadOnlyList<HideTarget>)` / `Unhide(IReadOnlyList<HideTarget>)` that iterate. The static `RequiresExplorerRestart` also gets a new overload.

- [ ] **Step 1: Write failing tests**

Append to `manager/test/RCMM.Tests/HideServiceTests.cs`:

```csharp
[Fact]
public void Hide_with_HideTarget_list_applies_LegacyDisable_to_each_target()
{
    var reg = new FakeRegistry();
    reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "", "Foo");
    reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\foo", "", "Foo");
    var sut = new HideService(reg);

    var targets = new[]
    {
        new HideTarget(HideKind.LegacyDisable, RegistryHive.ClassesRoot, @"*\shell\foo", "LegacyDisable"),
        new HideTarget(HideKind.LegacyDisable, RegistryHive.ClassesRoot, @"Directory\shell\foo", "LegacyDisable"),
    };
    sut.Hide(targets);

    Assert.Equal("", reg.GetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "LegacyDisable"));
    Assert.Equal("", reg.GetValue(RegistryHive.ClassesRoot, @"Directory\shell\foo", "LegacyDisable"));
}

[Fact]
public void Unhide_with_HideTarget_list_removes_LegacyDisable_from_each_target()
{
    var reg = new FakeRegistry();
    reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "LegacyDisable", "");
    reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\foo", "LegacyDisable", "");
    var sut = new HideService(reg);

    var targets = new[]
    {
        new HideTarget(HideKind.LegacyDisable, RegistryHive.ClassesRoot, @"*\shell\foo", "LegacyDisable"),
        new HideTarget(HideKind.LegacyDisable, RegistryHive.ClassesRoot, @"Directory\shell\foo", "LegacyDisable"),
    };
    sut.Unhide(targets);

    Assert.Null(reg.GetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "LegacyDisable"));
    Assert.Null(reg.GetValue(RegistryHive.ClassesRoot, @"Directory\shell\foo", "LegacyDisable"));
}

[Fact]
public void Hide_with_HkcuMask_target_creates_mask_key()
{
    var reg = new FakeRegistry();
    var sut = new HideService(reg);

    var targets = new[]
    {
        new HideTarget(HideKind.HkcuMask, RegistryHive.CurrentUser,
                       @"Software\Classes\*\shellex\ContextMenuHandlers\X", null),
    };
    sut.Hide(targets);

    Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
        @"Software\Classes\*\shellex\ContextMenuHandlers\X"));
}

[Fact]
public void Unhide_with_HkcuMask_target_deletes_mask_key()
{
    var reg = new FakeRegistry();
    reg.CreateKey(RegistryHive.CurrentUser, @"Software\Classes\*\shellex\ContextMenuHandlers\X");
    var sut = new HideService(reg);

    var targets = new[]
    {
        new HideTarget(HideKind.HkcuMask, RegistryHive.CurrentUser,
                       @"Software\Classes\*\shellex\ContextMenuHandlers\X", null),
    };
    sut.Unhide(targets);

    Assert.False(reg.KeyExists(RegistryHive.CurrentUser,
        @"Software\Classes\*\shellex\ContextMenuHandlers\X"));
}

[Fact]
public void RequiresExplorerRestart_list_overload_is_true_when_any_target_is_HkcuMask()
{
    var verb = new HideTarget(HideKind.LegacyDisable, RegistryHive.ClassesRoot, "p", "v");
    var mask = new HideTarget(HideKind.HkcuMask, RegistryHive.CurrentUser, "p", null);
    Assert.False(HideService.RequiresExplorerRestart(new[] { verb }));
    Assert.True(HideService.RequiresExplorerRestart(new[] { verb, mask }));
}
```

Add the `using` if needed: `using RCMM.Core.Models;`.

- [ ] **Step 2: Run tests — verify they fail to compile**

```powershell
dotnet test manager/test/RCMM.Tests
```

- [ ] **Step 3: Extend `HideService.cs`**

Add (don't replace) to `manager/src/RCMM.Core/Services/HideService.cs`:

```csharp
public void Hide(IReadOnlyList<HideTarget> targets)
{
    foreach (var t in targets)
    {
        if (t.Kind == HideKind.LegacyDisable)
        {
            _reg.SetValue(t.Hive, t.Path, t.ValueName ?? "LegacyDisable", "");
        }
        else // HkcuMask
        {
            _reg.CreateKey(t.Hive, t.Path);
            _reg.SetValue(t.Hive, t.Path, "", "");
        }
    }
}

public void Unhide(IReadOnlyList<HideTarget> targets)
{
    foreach (var t in targets)
    {
        if (t.Kind == HideKind.LegacyDisable)
        {
            _reg.DeleteValue(t.Hive, t.Path, t.ValueName ?? "LegacyDisable");
        }
        else // HkcuMask
        {
            _reg.DeleteKey(t.Hive, t.Path);
        }
    }
}

public static bool RequiresExplorerRestart(IReadOnlyList<HideTarget> targets)
{
    foreach (var t in targets)
        if (t.Kind == HideKind.HkcuMask) return true;
    return false;
}
```

Add `using System.Collections.Generic;` and `using RCMM.Core.Models;` at the top of the file if not present.

- [ ] **Step 4: Run tests, verify 5 new tests pass**

```powershell
dotnet test manager/test/RCMM.Tests
```

- [ ] **Step 5: Commit**

```powershell
git add manager
git commit -m "services: HideService.Hide/Unhide overloads for HideTarget lists"
```

---

### Task 3: VerbToRegistryMapper — verbs

**Files:**
- Create: `manager/src/RCMM.Core/Services/VerbToRegistryMapper.cs`
- Create: `manager/test/RCMM.Tests/VerbToRegistryMapperTests.cs`

The mapper reuses the existing scope-root list and `IRegistry` to find every `HKCR\<root>\shell\<verb>` location.

- [ ] **Step 1: Failing tests**

`manager/test/RCMM.Tests/VerbToRegistryMapperTests.cs`:

```csharp
using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class VerbToRegistryMapperTests
{
    [Fact]
    public void Map_verb_finds_no_targets_when_unregistered()
    {
        var reg = new FakeRegistry();
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapVerb("nonexistent").ToList();

        Assert.Empty(targets);
    }

    [Fact]
    public void Map_verb_finds_single_registry_location()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\git_shell", "", "Open Git Bash here");
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapVerb("git_shell").ToList();

        Assert.Single(targets);
        Assert.Equal(HideKind.LegacyDisable, targets[0].Kind);
        Assert.Equal(RegistryHive.ClassesRoot, targets[0].Hive);
        Assert.Equal(@"*\shell\git_shell", targets[0].Path);
        Assert.Equal("LegacyDisable", targets[0].ValueName);
    }

    [Fact]
    public void Map_verb_finds_all_scope_root_locations()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\open", "", "Open");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\open", "", "Open");
        reg.SetValue(RegistryHive.ClassesRoot, @"Drive\shell\open", "", "Open");
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapVerb("open").ToList();

        Assert.Equal(3, targets.Count);
        Assert.Contains(targets, t => t.Path == @"*\shell\open");
        Assert.Contains(targets, t => t.Path == @"Directory\shell\open");
        Assert.Contains(targets, t => t.Path == @"Drive\shell\open");
    }

    [Fact]
    public void Map_clsid_finds_shellex_handler_locations_via_default_or_keyname_match()
    {
        var reg = new FakeRegistry();
        // Handler registered with CLSID in default value:
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\WinRAR", "", "{ABC}");
        // Handler registered with CLSID as key name:
        reg.CreateKey(RegistryHive.ClassesRoot, @"Directory\shellex\ContextMenuHandlers\{ABC}");
        var sut = new VerbToRegistryMapper(reg);

        var targets = sut.MapClsid("{ABC}").ToList();

        Assert.Equal(2, targets.Count);
        Assert.All(targets, t => Assert.Equal(HideKind.HkcuMask, t.Kind));
        Assert.All(targets, t => Assert.Equal(RegistryHive.CurrentUser, t.Hive));
        Assert.Contains(targets, t => t.Path == @"Software\Classes\*\shellex\ContextMenuHandlers\WinRAR");
        Assert.Contains(targets, t => t.Path == @"Software\Classes\Directory\shellex\ContextMenuHandlers\{ABC}");
    }
}
```

- [ ] **Step 2: Implement `VerbToRegistryMapper.cs`**

```csharp
using System;
using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public sealed class VerbToRegistryMapper
{
    private static readonly Scope[] AllScopes =
    {
        Scope.Files, Scope.Folders, Scope.Drives, Scope.Background,
        Scope.AllObjects, Scope.Folder
    };

    private readonly IRegistry _reg;

    public VerbToRegistryMapper(IRegistry reg) { _reg = reg; }

    public IEnumerable<HideTarget> MapVerb(string verb)
    {
        foreach (var scope in AllScopes)
        {
            var root = scope.ToRegistryRoot() + @"\shell\" + verb;
            if (_reg.KeyExists(RegistryHive.ClassesRoot, root))
            {
                yield return new HideTarget(
                    HideKind.LegacyDisable,
                    RegistryHive.ClassesRoot,
                    root,
                    "LegacyDisable");
            }
        }
    }

    public IEnumerable<HideTarget> MapClsid(string clsid)
    {
        foreach (var scope in AllScopes)
        {
            var handlersRoot = scope.ToRegistryRoot() + @"\shellex\ContextMenuHandlers";
            if (!_reg.KeyExists(RegistryHive.ClassesRoot, handlersRoot)) continue;
            foreach (var name in _reg.GetSubKeyNames(RegistryHive.ClassesRoot, handlersRoot))
            {
                var defaultVal = _reg.GetValue(RegistryHive.ClassesRoot, handlersRoot + "\\" + name, "") as string;
                var match = (defaultVal != null && string.Equals(defaultVal, clsid, StringComparison.OrdinalIgnoreCase))
                            || string.Equals(name, clsid, StringComparison.OrdinalIgnoreCase);
                if (match)
                {
                    yield return new HideTarget(
                        HideKind.HkcuMask,
                        RegistryHive.CurrentUser,
                        @"Software\Classes\" + scope.ToRegistryRoot() + @"\shellex\ContextMenuHandlers\" + name,
                        null);
                }
            }
        }
    }
}
```

- [ ] **Step 3: Tests pass**

```powershell
dotnet test manager/test/RCMM.Tests
```

Expected: 4 new tests pass.

- [ ] **Step 4: Commit**

```powershell
git add manager
git commit -m "services: VerbToRegistryMapper fans verbs/CLSIDs across all scope roots"
```

---

### Task 4: IContextMenuCaptureService interface + FakeContextMenuCaptureService

**Files:**
- Create: `manager/src/RCMM.Core/Services/IContextMenuCaptureService.cs`
- Create: `manager/test/RCMM.Tests/FakeContextMenuCaptureService.cs`

- [ ] **Step 1: Interface**

```csharp
using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public interface IContextMenuCaptureService
{
    /// <summary>
    /// Captures the context menu for each provided target path and returns
    /// one CapturedItem per (target × menu item) pair. No deduplication —
    /// callers (MainViewModel) handle merging.
    /// </summary>
    IReadOnlyList<CapturedItem> CaptureAll(IReadOnlyList<string> targetPaths);
}
```

- [ ] **Step 2: Fake**

```csharp
using System.Collections.Generic;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Tests;

public sealed class FakeContextMenuCaptureService : IContextMenuCaptureService
{
    public Dictionary<string, List<CapturedItem>> Map { get; } = new();

    public IReadOnlyList<CapturedItem> CaptureAll(IReadOnlyList<string> targetPaths)
    {
        var result = new List<CapturedItem>();
        foreach (var path in targetPaths)
        {
            if (Map.TryGetValue(path, out var items))
                result.AddRange(items);
        }
        return result;
    }
}
```

- [ ] **Step 3: Build**

```powershell
dotnet build manager/RCMM.sln
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```powershell
git add manager
git commit -m "services: IContextMenuCaptureService + FakeContextMenuCaptureService"
```

---

### Task 5: TargetProvider

**Files:**
- Create: `manager/src/RCMM.Core/Services/TargetProvider.cs`
- Create: `manager/test/RCMM.Tests/TargetProviderTests.cs`

`TargetProvider` produces the 10 representative paths. For file samples it creates 0-byte files in a temp folder under `%TEMP%\rcmm-capture\`. For folder/background it reuses the temp folder itself. For drive it uses `C:\` (or the first available drive).

- [ ] **Step 1: Failing tests**

```csharp
using System.IO;
using System.Linq;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class TargetProviderTests : System.IDisposable
{
    private readonly string _root;
    private readonly TargetProvider _sut;

    public TargetProviderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rcmm-test-{System.Guid.NewGuid():N}");
        _sut = new TargetProvider(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void GetTargets_creates_temp_files_for_each_sample_extension()
    {
        var targets = _sut.GetTargets().ToList();

        Assert.Contains(targets, p => p.EndsWith(".txt"));
        Assert.Contains(targets, p => p.EndsWith(".png"));
        Assert.Contains(targets, p => p.EndsWith(".mp4"));
        Assert.Contains(targets, p => p.EndsWith(".mp3"));
        Assert.Contains(targets, p => p.EndsWith(".pdf"));
        Assert.Contains(targets, p => p.EndsWith(".zip"));
        Assert.Contains(targets, p => p.EndsWith(".exe"));
        Assert.Contains(targets, p => p.EndsWith(".lnk"));
        foreach (var t in targets.Where(p => Path.HasExtension(p)))
            Assert.True(File.Exists(t), $"expected file to exist: {t}");
    }

    [Fact]
    public void GetTargets_includes_folder_and_drive()
    {
        var targets = _sut.GetTargets().ToList();
        Assert.Contains(_root, targets);
        Assert.Contains(targets, p => p.Length == 3 && p[1] == ':' && p[2] == '\\');
    }

    [Fact]
    public void GetTargets_is_idempotent()
    {
        var first = _sut.GetTargets().ToList();
        var second = _sut.GetTargets().ToList();
        Assert.Equal(first, second);
    }
}
```

- [ ] **Step 2: Implement `TargetProvider.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RCMM.Core.Services;

public sealed class TargetProvider
{
    private static readonly string[] SampleExtensions =
        { ".txt", ".png", ".mp4", ".mp3", ".pdf", ".zip", ".exe", ".lnk" };

    private readonly string _root;

    public TargetProvider() : this(DefaultRoot()) { }
    public TargetProvider(string root) { _root = root; }

    public static string DefaultRoot()
        => Path.Combine(Path.GetTempPath(), "rcmm-capture");

    public IReadOnlyList<string> GetTargets()
    {
        Directory.CreateDirectory(_root);

        var result = new List<string> { _root };

        var firstDrive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady)?.RootDirectory.FullName;
        if (firstDrive != null) result.Add(firstDrive);

        foreach (var ext in SampleExtensions)
        {
            var path = Path.Combine(_root, "sample" + ext);
            if (!File.Exists(path))
            {
                try { using (File.Create(path)) { } }
                catch { continue; }
            }
            result.Add(path);
        }

        return result;
    }

    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }
}
```

- [ ] **Step 3: Run tests**

```powershell
dotnet test manager/test/RCMM.Tests
```

Expected: 3 new tests pass.

- [ ] **Step 4: Commit**

```powershell
git add manager
git commit -m "services: TargetProvider with temp file lifecycle"
```

---

### Task 6: Refactor EntryRowViewModel to wrap MenuEntry

**Files:**
- Modify: `manager/src/RCMM.Core/ViewModels/EntryRowViewModel.cs`

The wrapper exposes the bind-friendly properties on top of `MenuEntry`. Same shape as before but the underlying entry changes type.

- [ ] **Step 1: Replace `EntryRowViewModel.cs`**

```csharp
using System;
using RCMM.Core.Models;

namespace RCMM.Core.ViewModels;

public sealed class EntryRowViewModel : ObservableObject
{
    private bool _isHidden;
    private object? _icon;

    public MenuEntry Entry { get; }
    public Action<EntryRowViewModel, bool>? HiddenChanged;

    public EntryRowViewModel(MenuEntry entry)
    {
        Entry = entry;
        _isHidden = entry.IsHidden;
    }

    public string DisplayName => Entry.DisplayName;
    public string Source => string.IsNullOrEmpty(Entry.Source) ? "Unknown" : Entry.Source!;
    public string KindLabel => Entry.IsSubmenu ? "Submenu" : "Item";
    public bool IsBuiltIn => Entry.IsBuiltIn;
    public bool CanHide => Entry.CanHide;
    public byte[]? IconBytes => Entry.IconBytes;

    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (!CanHide) return;
            if (SetField(ref _isHidden, value))
                HiddenChanged?.Invoke(this, value);
        }
    }

    public object? Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }
}
```

NOTE: removed the `KindLabel` switch on `EntryKind.ShellVerb / ShellExtension` since `MenuEntry` no longer carries that distinction directly. We expose `IsSubmenu` instead, which is what users actually care about visually.

- [ ] **Step 2: Build (some compile errors expected in MainViewModel — those land in the next task)**

```powershell
dotnet build manager/RCMM.sln 2>&1 | findstr /R "error"
```

Expected error pattern: `MainViewModel.cs(...): error CS0117: 'EntryRowViewModel'` or similar — confined to MainViewModel.cs. Other projects build clean.

If the test project also fails to compile (it references EntryRowViewModel via FakeRegistry-using tests), those failures are also expected; they're addressed in Task 7.

- [ ] **Step 3: Do NOT commit yet — wait for Task 7 to land a buildable state**

---

### Task 7: Rewrite MainViewModel to drive capture + map

**Files:**
- Rewrite: `manager/src/RCMM.Core/ViewModels/MainViewModel.cs`
- Rewrite: `manager/test/RCMM.Tests/MainViewModelTests.cs`

This is the integrative task. Removes the old per-scope dictionaries, replaces with a `Rescan` that runs capture → merge → map → MenuEntry → AllEntries.

- [ ] **Step 1: Rewrite `MainViewModel.cs`**

```csharp
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Core.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IContextMenuCaptureService _capture;
    private readonly TargetProvider _targets;
    private readonly VerbToRegistryMapper _mapper;
    private readonly HideService _hideService;
    private readonly IRegistry _reg;

    private readonly Dictionary<string, IReadOnlyList<HideTarget>> _pendingHide = new();
    private readonly Dictionary<string, IReadOnlyList<HideTarget>> _pendingUnhide = new();
    private bool _showBuiltIns = true;

    public ObservableCollection<EntryRowViewModel> AllEntries { get; } = new();
    public ObservableCollection<string> PendingChangeIds { get; } = new();

    public MainViewModel(
        IContextMenuCaptureService capture,
        TargetProvider targets,
        VerbToRegistryMapper mapper,
        HideService hideService,
        IRegistry reg)
    {
        _capture = capture;
        _targets = targets;
        _mapper = mapper;
        _hideService = hideService;
        _reg = reg;
    }

    public bool RequiresExplorerRestart
        => _pendingHide.Values.Concat(_pendingUnhide.Values)
                       .SelectMany(t => t)
                       .Any(t => t.Kind == HideKind.HkcuMask);

    public bool ShowBuiltIns
    {
        get => _showBuiltIns;
        set
        {
            if (SetField(ref _showBuiltIns, value))
                FilterIntoAllEntries();
        }
    }

    private readonly List<EntryRowViewModel> _allRows = new();

    public void Rescan()
    {
        var captures = _capture.CaptureAll(_targets.GetTargets());
        var merged = MergeCaptures(captures);

        _allRows.Clear();
        foreach (var item in merged)
        {
            var hideTargets = ResolveHideTargets(item);
            var iconPath = ResolveIconPath(item, hideTargets);
            var isHidden = AllTargetsHidden(hideTargets);
            var entry = new MenuEntry
            {
                Id = ComputeId(item),
                DisplayName = item.DisplayName,
                Source = null,
                IconBytes = item.IconBytes,
                IconPath = iconPath,
                HideTargets = hideTargets,
                IsBuiltIn = false,
                IsHidden = isHidden,
                IsSubmenu = item.IsSubmenu
            };
            var row = new EntryRowViewModel(entry) { HiddenChanged = OnRowToggled };
            _allRows.Add(row);
        }

        FilterIntoAllEntries();
        _pendingHide.Clear();
        _pendingUnhide.Clear();
        PendingChangeIds.Clear();
        Raise(nameof(RequiresExplorerRestart));
    }

    private void FilterIntoAllEntries()
    {
        AllEntries.Clear();
        foreach (var row in _allRows)
        {
            if (row.IsBuiltIn && !_showBuiltIns) continue;
            AllEntries.Add(row);
        }
    }

    private static IEnumerable<CapturedItem> MergeCaptures(IEnumerable<CapturedItem> captures)
    {
        var seen = new HashSet<string>();
        foreach (var item in captures)
        {
            if (item.IsSeparator) continue;
            var key = MergeKey(item);
            if (seen.Add(key)) yield return item;
        }
    }

    private static string MergeKey(CapturedItem item)
    {
        if (!string.IsNullOrEmpty(item.Verb)) return "verb:" + item.Verb.ToLowerInvariant();
        if (!string.IsNullOrEmpty(item.OwnerClsid)) return "clsid:" + item.OwnerClsid.ToLowerInvariant();
        return "name:" + item.DisplayName.ToLowerInvariant();
    }

    private static string ComputeId(CapturedItem item) => MergeKey(item);

    private IReadOnlyList<HideTarget> ResolveHideTargets(CapturedItem item)
    {
        var result = new List<HideTarget>();
        if (!string.IsNullOrEmpty(item.Verb))
            result.AddRange(_mapper.MapVerb(item.Verb));
        if (!string.IsNullOrEmpty(item.OwnerClsid))
            result.AddRange(_mapper.MapClsid(item.OwnerClsid));
        return result;
    }

    private string? ResolveIconPath(CapturedItem item, IReadOnlyList<HideTarget> targets)
    {
        // Prefer the verb's Icon registry value; fall back to the verb's command-line exe.
        foreach (var target in targets)
        {
            if (target.Kind != HideKind.LegacyDisable) continue;
            var icon = _reg.GetValue(target.Hive, target.Path, "Icon") as string;
            if (!string.IsNullOrWhiteSpace(icon)) return icon;
            var cmd = _reg.GetValue(target.Hive, target.Path + @"\command", "") as string;
            if (!string.IsNullOrWhiteSpace(cmd)) return cmd;
        }
        return null;
    }

    private bool AllTargetsHidden(IReadOnlyList<HideTarget> targets)
    {
        if (targets.Count == 0) return false;
        foreach (var t in targets)
        {
            if (t.Kind == HideKind.LegacyDisable)
            {
                if (_reg.GetValue(t.Hive, t.Path, t.ValueName ?? "LegacyDisable") == null) return false;
            }
            else
            {
                if (!_reg.KeyExists(t.Hive, t.Path)) return false;
            }
        }
        return true;
    }

    private void OnRowToggled(EntryRowViewModel row, bool isHidden)
    {
        var id = row.Entry.Id;
        // If the new state matches what's already on disk, clear any pending changes.
        if (isHidden == AllTargetsHidden(row.Entry.HideTargets))
        {
            _pendingHide.Remove(id);
            _pendingUnhide.Remove(id);
            PendingChangeIds.Remove(id);
        }
        else if (isHidden)
        {
            _pendingHide[id] = row.Entry.HideTargets;
            _pendingUnhide.Remove(id);
            if (!PendingChangeIds.Contains(id)) PendingChangeIds.Add(id);
        }
        else
        {
            _pendingUnhide[id] = row.Entry.HideTargets;
            _pendingHide.Remove(id);
            if (!PendingChangeIds.Contains(id)) PendingChangeIds.Add(id);
        }
        Raise(nameof(RequiresExplorerRestart));
    }

    public void ApplyPending()
    {
        foreach (var (_, targets) in _pendingHide) _hideService.Hide(targets);
        foreach (var (_, targets) in _pendingUnhide) _hideService.Unhide(targets);
        _pendingHide.Clear();
        _pendingUnhide.Clear();
        PendingChangeIds.Clear();
        Raise(nameof(RequiresExplorerRestart));
    }
}
```

- [ ] **Step 2: Rewrite `MainViewModelTests.cs`**

```csharp
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;
using Xunit;

namespace RCMM.Tests;

public class MainViewModelTests : System.IDisposable
{
    private readonly string _tempRoot;
    private readonly TargetProvider _targets;

    public MainViewModelTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"rcmm-mvm-{System.Guid.NewGuid():N}");
        _targets = new TargetProvider(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private (MainViewModel vm, FakeRegistry reg, FakeContextMenuCaptureService cap) BuildSut()
    {
        var reg = new FakeRegistry();
        var cap = new FakeContextMenuCaptureService();
        var mapper = new VerbToRegistryMapper(reg);
        var hide = new HideService(reg);
        var vm = new MainViewModel(cap, _targets, mapper, hide, reg);
        return (vm, reg, cap);
    }

    private string FirstFileTarget() => _targets.GetTargets().First(p => p.EndsWith(".txt"));

    [Fact]
    public void Rescan_populates_AllEntries_from_captured_items()
    {
        var (vm, reg, cap) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\git_shell", "", "Open Git Bash here");

        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "Open Git Bash here", Verb = "git_shell" }
        };

        vm.Rescan();

        Assert.Single(vm.AllEntries);
        Assert.Equal("Open Git Bash here", vm.AllEntries[0].DisplayName);
        Assert.True(vm.AllEntries[0].CanHide);
    }

    [Fact]
    public void Rescan_dedupes_same_verb_across_multiple_targets()
    {
        var (vm, reg, cap) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\open", "", "Open");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\open", "", "Open");

        foreach (var target in _targets.GetTargets())
        {
            cap.Map[target] = new List<CapturedItem>
            {
                new() { TargetPath = target, Position = 0, DisplayName = "Open", Verb = "open" }
            };
        }

        vm.Rescan();

        Assert.Single(vm.AllEntries);
    }

    [Fact]
    public void Captured_item_with_no_registry_match_is_marked_as_unhideable()
    {
        var (vm, _, cap) = BuildSut();
        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "Cut", Verb = "cut" }
        };

        vm.Rescan();

        Assert.Single(vm.AllEntries);
        Assert.False(vm.AllEntries[0].CanHide);
    }

    [Fact]
    public void Toggle_records_pending_hide_with_full_target_list()
    {
        var (vm, reg, cap) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "", "Foo");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\foo", "", "Foo");

        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "Foo", Verb = "foo" }
        };

        vm.Rescan();
        vm.AllEntries[0].IsHidden = true;

        Assert.Single(vm.PendingChangeIds);
    }

    [Fact]
    public void ApplyPending_writes_LegacyDisable_to_every_HideTarget()
    {
        var (vm, reg, cap) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "", "Foo");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\foo", "", "Foo");

        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "Foo", Verb = "foo" }
        };

        vm.Rescan();
        vm.AllEntries[0].IsHidden = true;
        vm.ApplyPending();

        Assert.Equal("", reg.GetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "LegacyDisable"));
        Assert.Equal("", reg.GetValue(RegistryHive.ClassesRoot, @"Directory\shell\foo", "LegacyDisable"));
    }

    [Fact]
    public void Shellex_toggle_sets_RequiresExplorerRestart()
    {
        var (vm, reg, cap) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\WinRAR", "", "{ABC}");

        var target = FirstFileTarget();
        cap.Map[target] = new List<CapturedItem>
        {
            new() { TargetPath = target, Position = 0, DisplayName = "WinRAR", OwnerClsid = "{ABC}" }
        };

        vm.Rescan();
        vm.AllEntries[0].IsHidden = true;

        Assert.True(vm.RequiresExplorerRestart);
    }
}
```

- [ ] **Step 3: Build and test**

```powershell
dotnet build manager/RCMM.sln
dotnet test manager/test/RCMM.Tests
```

Expected: 0 errors, all tests pass (6 new MainViewModel tests + everything else still green). The existing `ClassicVerbScannerTests`, `ClassicShellexScannerTests`, `EntryScannerTests`, `HideServiceTests`, `EntryFiltersTests`, etc. should continue passing.

- [ ] **Step 4: Commit (lands Tasks 6 and 7 together)**

```powershell
git add manager
git commit -m "viewmodel: rewrite MainViewModel around capture + map; EntryRowViewModel wraps MenuEntry"
```

---

### Task 8: ShellInterop — P/Invoke and COM type definitions

**Files:**
- Create: `manager/src/RCMM.Core/Interop/ShellInterop.cs`

Pure P/Invoke. No tests — exercised by the next task.

- [ ] **Step 1: Create `ShellInterop.cs`**

```csharp
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RCMM.Core.Interop;

internal static class ShellInterop
{
    // === GUIDs ===
    internal static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    internal static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");
    internal static readonly Guid BHID_SFUIObject = new("3981E224-F559-11D3-8E3A-00C04F6837D5");

    // === SHCreateItemFromParsingName ===
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IShellItem ppv);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(int sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    // === IContextMenu ===
    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(IntPtr pici);
        [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved,
                                            [Out] byte[] pszName, uint cchMax);
    }

    // === HMENU ===
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMenuItemCount(IntPtr hMenu);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MENUITEMINFOW
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMenuItemInfoW(
        IntPtr hMenu,
        uint uItem,
        [MarshalAs(UnmanagedType.Bool)] bool fByPosition,
        ref MENUITEMINFOW lpmii);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetMenuStringW(
        IntPtr hMenu,
        uint uIDItem,
        StringBuilder lpString,
        int cchMax,
        uint flags);

    // === Constants ===
    internal const uint CMF_NORMAL = 0;
    internal const uint CMF_EXTENDEDVERBS = 0x100;

    internal const uint MIIM_ID = 0x2;
    internal const uint MIIM_SUBMENU = 0x4;
    internal const uint MIIM_TYPE = 0x10;
    internal const uint MIIM_STRING = 0x40;
    internal const uint MIIM_BITMAP = 0x80;
    internal const uint MIIM_FTYPE = 0x100;

    internal const uint MFT_STRING = 0;
    internal const uint MFT_BITMAP = 0x4;
    internal const uint MFT_OWNERDRAW = 0x100;
    internal const uint MFT_SEPARATOR = 0x800;

    internal const uint MF_BYPOSITION = 0x400;
    internal const uint MF_BYCOMMAND = 0x0;

    internal const uint GCS_VERBA = 0;
    internal const uint GCS_HELPTEXTA = 1;
    internal const uint GCS_VALIDATEA = 2;
    internal const uint GCS_VERBW = 4;
    internal const uint GCS_HELPTEXTW = 5;
    internal const uint GCS_VALIDATEW = 6;

    // === CoInitialize ===
    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);
    internal const uint COINIT_APARTMENTTHREADED = 0x2;

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();

    // === CoTaskMemFree (for IShellItem::GetDisplayName output) ===
    [DllImport("ole32.dll")]
    internal static extern void CoTaskMemFree(IntPtr ptr);
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build manager/RCMM.sln
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```powershell
git add manager
git commit -m "interop: ShellInterop P/Invoke + COM definitions for IContextMenu capture"
```

---

### Task 9: ContextMenuCaptureService — basic capture loop on STA worker

**Files:**
- Create: `manager/src/RCMM.Core/Services/ContextMenuCaptureService.cs`

Real implementation. Runs CaptureAll on a dedicated STA worker thread per call. Captures verb names but no bitmaps yet (those land in a follow-up if needed; menu-bitmap extraction is an extra-credit nicety).

- [ ] **Step 1: Create `ContextMenuCaptureService.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RCMM.Core.Interop;
using RCMM.Core.Models;
using static RCMM.Core.Interop.ShellInterop;

namespace RCMM.Core.Services;

public sealed class ContextMenuCaptureService : IContextMenuCaptureService
{
    public IReadOnlyList<CapturedItem> CaptureAll(IReadOnlyList<string> targetPaths)
    {
        var result = new List<CapturedItem>();
        var done = new ManualResetEventSlim(false);

        var t = new Thread(() =>
        {
            CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
            try
            {
                foreach (var path in targetPaths)
                {
                    try { result.AddRange(CaptureOne(path)); }
                    catch { /* per-target failures don't kill the batch */ }
                }
            }
            finally
            {
                CoUninitialize();
                done.Set();
            }
        })
        { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        done.Wait();

        return result;
    }

    private static IEnumerable<CapturedItem> CaptureOne(string targetPath)
    {
        var iidShellItem = IID_IShellItem;
        var iidContextMenu = IID_IContextMenu;
        var bhid = BHID_SFUIObject;

        int hr = SHCreateItemFromParsingName(targetPath, IntPtr.Zero, ref iidShellItem, out var psi);
        if (hr != 0 || psi == null) yield break;

        IContextMenu? pcm = null;
        IntPtr hMenu = IntPtr.Zero;
        try
        {
            hr = psi.BindToHandler(IntPtr.Zero, ref bhid, ref iidContextMenu, out var pcmPtr);
            if (hr != 0 || pcmPtr == IntPtr.Zero) yield break;
            pcm = (IContextMenu)Marshal.GetObjectForIUnknown(pcmPtr);
            Marshal.Release(pcmPtr);

            hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero) yield break;

            const int idCmdFirst = 1;
            const int idCmdLast = 0x7FFF;
            hr = pcm.QueryContextMenu(hMenu, 0, idCmdFirst, idCmdLast, CMF_NORMAL | CMF_EXTENDEDVERBS);
            if (hr < 0) yield break;

            int count = GetMenuItemCount(hMenu);
            for (int i = 0; i < count; i++)
            {
                var item = ReadMenuItem(hMenu, i, pcm, idCmdFirst, targetPath);
                if (item != null) yield return item;
            }
        }
        finally
        {
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (pcm != null) Marshal.ReleaseComObject(pcm);
            if (psi != null) Marshal.ReleaseComObject(psi);
        }
    }

    private static CapturedItem? ReadMenuItem(IntPtr hMenu, int position, IContextMenu pcm, int idCmdFirst, string targetPath)
    {
        var mii = new MENUITEMINFOW
        {
            cbSize = (uint)Marshal.SizeOf<MENUITEMINFOW>(),
            fMask = MIIM_ID | MIIM_FTYPE | MIIM_SUBMENU
        };
        if (!GetMenuItemInfoW(hMenu, (uint)position, true, ref mii))
            return null;

        bool isSeparator = (mii.fType & MFT_SEPARATOR) != 0;
        bool isSubmenu = mii.hSubMenu != IntPtr.Zero;

        if (isSeparator)
        {
            return new CapturedItem
            {
                TargetPath = targetPath,
                Position = position,
                DisplayName = "",
                IsSeparator = true,
                IsSubmenu = false
            };
        }

        // Resolve the displayed text
        var sb = new StringBuilder(512);
        GetMenuStringW(hMenu, (uint)position, sb, sb.Capacity, MF_BYPOSITION);
        var display = sb.ToString();
        if (string.IsNullOrEmpty(display)) display = "(unnamed)";
        display = StripAccelerator(display);

        // Resolve the canonical verb
        string? verb = null;
        try
        {
            var buf = new byte[512];
            uint idLocal = mii.wID >= idCmdFirst ? (uint)(mii.wID - idCmdFirst) : mii.wID;
            int hr = pcm.GetCommandString((IntPtr)idLocal, GCS_VERBW, IntPtr.Zero, buf, (uint)buf.Length);
            if (hr == 0)
            {
                verb = Encoding.Unicode.GetString(buf).TrimEnd('\0');
                if (string.IsNullOrWhiteSpace(verb)) verb = null;
            }
        }
        catch { verb = null; }

        return new CapturedItem
        {
            TargetPath = targetPath,
            Position = position,
            DisplayName = display,
            Verb = verb,
            IsSeparator = false,
            IsSubmenu = isSubmenu
        };
    }

    private static string StripAccelerator(string s)
        => s.Replace("&&", "￾").Replace("&", "").Replace("￾", "&");
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build manager/RCMM.sln
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```powershell
git add manager
git commit -m "services: ContextMenuCaptureService — STA worker capturing display + verb"
```

---

### Task 10: Wire MainWindow.xaml.cs to use new services

**Files:**
- Modify: `manager/src/RCMM/MainWindow.xaml.cs`

Replace the scanner-driven DI with capture-driven DI. The old `ContextMenuEntry`-based icon loading goes away (icons now flow from `CapturedItem.IconBytes` directly, or — for now, while bitmap extraction isn't wired up — they're null and the row shows no icon).

- [ ] **Step 1: Modify `MainWindow.xaml.cs`**

Replace the constructor body's service-building section and the post-Rescan icon loader. Here's the full updated constructor + helpers:

```csharp
public MainWindow()
{
    InitializeComponent();

    var registry = new Win32Registry();
    var capture = new ContextMenuCaptureService();
    var targets = new TargetProvider();
    var mapper = new VerbToRegistryMapper(registry);
    var hide = new HideService(registry);

    ViewModel = new MainViewModel(capture, targets, mapper, hide, registry);

    ExtendsContentIntoTitleBar = true;
    SetTitleBar(AppTitleBar);
    TryRemoveWindowBorder();
    _minSize = WindowMinSize.Apply(WinRT.Interop.WindowNative.GetWindowHandle(this), 600, 480);

    HookThemeChange();
    ViewModel.PropertyChanged += OnVmPropertyChanged;
    ViewModel.PendingChangeIds.CollectionChanged += (_, __) => RefreshFooter();
    ViewModel.Rescan();
    LoadIconsForAllEntries();
    RefreshFooter();

    ContentFrame.Navigate(typeof(ScopePage), ViewModel);
}
```

`LoadIconsForAllEntries` now has two paths: decode `IconBytes` if present (capture-side bitmap), or fall back to the existing `IconHelper.LoadIconAsync(MenuEntry.IconPath)` (registry-resolved DLL/exe path). The path-based fallback is what carries icons in v0.2 since the capture doesn't yet extract MIIM_BITMAP.

```csharp
private void LoadIconsForAllEntries()
{
    foreach (var row in ViewModel.AllEntries)
    {
        var rowRef = row;
        var bytes = row.Entry.IconBytes;
        if (bytes != null && bytes.Length > 0)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    using var ms = new System.IO.MemoryStream(bytes);
                    var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    await bmp.SetSourceAsync(ms.AsRandomAccessStream());
                    rowRef.Icon = bmp;
                }
                catch { /* ignore */ }
            });
            continue;
        }

        var path = row.Entry.IconPath;
        if (string.IsNullOrEmpty(path)) continue;
        _ = Task.Run(async () =>
        {
            var bmp = await IconHelper.LoadIconAsync(path);
            if (bmp != null)
                DispatcherQueue.TryEnqueue(() => rowRef.Icon = bmp);
        });
    }
}
```

Keep `using System.IO;` and `using System.Threading.Tasks;` at the top. `IconHelper` is the existing helper from Plan 1. `ExtractExeFromCommand` is no longer needed and can be removed — `IconHelper.LoadIconAsync` already strips trailing comma-indices and wrapping quotes, and `CommandLine` paths typically begin with the exe.

`RefreshFooter` updates to use the new `PendingChangeIds` collection:

```csharp
private void RefreshFooter()
{
    StatusLabel.Text = $"{ViewModel.AllEntries.Count} entries · {ViewModel.PendingChangeIds.Count} pending";
    ApplyButton.IsEnabled = ViewModel.PendingChangeIds.Count > 0;
}
```

`ApplyButton_Click` is essentially unchanged:

```csharp
private void ApplyButton_Click(object sender, RoutedEventArgs e)
{
    var needsRestart = ViewModel.RequiresExplorerRestart;
    ViewModel.ApplyPending();
    if (needsRestart) new ExplorerRestart().Restart();
    ViewModel.Rescan();
    LoadIconsForAllEntries();
    RefreshFooter();
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build manager/RCMM.sln
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```powershell
git add manager
git commit -m "ui: MainWindow uses ContextMenuCaptureService + new MainViewModel wiring"
```

---

### Task 11: ScopePage — disable toggle for un-hideable items

**Files:**
- Modify: `manager/src/RCMM/Views/ScopePage.xaml`

The ToggleSwitch binds `IsEnabled` to `CanHide`. A small TextBlock appears beside the switch when `CanHide` is false ("Built into Windows").

- [ ] **Step 1: Update the row DataTemplate**

Replace the existing ToggleSwitch line in `ScopePage.xaml` with:

```xml
<StackPanel Grid.Column="2" Orientation="Horizontal" Spacing="6" VerticalAlignment="Center">
    <TextBlock Text="Built into Windows" FontSize="10" Opacity="0.55"
               VerticalAlignment="Center"
               Visibility="{x:Bind CanHide, Converter={StaticResource InvertBoolToVisibilityConverter}}"/>
    <ToggleSwitch IsOn="{x:Bind IsHidden, Mode=TwoWay, Converter={StaticResource InvertBoolConverter}}"
                  IsEnabled="{x:Bind CanHide}"
                  OnContent="" OffContent=""/>
</StackPanel>
```

Note this uses a converter `InvertBoolToVisibilityConverter` we don't yet have. Create it:

`manager/src/RCMM/InvertBoolConverter.cs` — add a third converter class at the bottom:

```csharp
public sealed class InvertBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, System.Type t, object p, string l)
        => (bool)value ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, System.Type t, object p, string l)
        => (Visibility)value == Visibility.Collapsed;
}
```

Register it in `App.xaml` alongside the existing converters:

```xml
<local:InvertBoolToVisibilityConverter x:Key="InvertBoolToVisibilityConverter"/>
```

- [ ] **Step 2: Build**

```powershell
dotnet build manager/RCMM.sln
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```powershell
git add manager
git commit -m "ui: disable toggle and show 'Built into Windows' for un-hideable rows"
```

---

### Task 12: Cleanup — remove EntryFilters

**Files:**
- Delete: `manager/src/RCMM.Core/Util/EntryFilters.cs`
- Delete: `manager/test/RCMM.Tests/EntryFiltersTests.cs`

The capture-based list doesn't need this filter; it was used by Plan 1's MainViewModel which is now replaced.

- [ ] **Step 1: Delete the files**

```powershell
del manager\src\RCMM.Core\Util\EntryFilters.cs
del manager\test\RCMM.Tests\EntryFiltersTests.cs
```

- [ ] **Step 2: Build + test**

```powershell
dotnet build manager/RCMM.sln
dotnet test manager/test/RCMM.Tests
```

Expected: 0 errors, all remaining tests pass.

- [ ] **Step 3: Commit**

```powershell
git add manager
git commit -m "cleanup: remove EntryFilters — capture-based list is already correct"
```

---

### Task 13: Integration smoke test for ContextMenuCaptureService

**Files:**
- Create: `manager/test/RCMM.Tests/ContextMenuCaptureServiceTests.cs`

A real-COM test that captures the menu for `%TEMP%`'s sample.txt and asserts at least one well-known verb appears. Gated by `[Trait("Integration", "true")]` so it doesn't run in headless CI (though headless CI doesn't exist for this project right now; the trait is just for future-proofing and grep-ability).

- [ ] **Step 1: Create test**

```csharp
using System;
using System.IO;
using System.Linq;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

[Trait("Integration", "true")]
public class ContextMenuCaptureServiceTests : System.IDisposable
{
    private readonly string _tempFile;

    public ContextMenuCaptureServiceTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"rcmm-cap-{Guid.NewGuid():N}.txt");
        File.WriteAllText(_tempFile, "");
    }

    public void Dispose()
    {
        try { File.Delete(_tempFile); } catch { }
    }

    [Fact]
    public void CaptureAll_returns_items_for_a_temp_text_file()
    {
        var sut = new ContextMenuCaptureService();

        var captures = sut.CaptureAll(new[] { _tempFile });

        Assert.NotEmpty(captures);
        // "Open" is hardcoded into shell32 for every file — we should see it.
        Assert.Contains(captures, c => c.Verb == "open" || c.DisplayName.Equals("Open", StringComparison.OrdinalIgnoreCase));
    }
}
```

- [ ] **Step 2: Run**

```powershell
dotnet test manager/test/RCMM.Tests
```

Expected: PASS for the integration test on Windows. If it fails because the test runner doesn't have an STA thread available or COM init returns an unexpected hr, document the failure and skip the test in the next iteration (do NOT change production code to make a real-COM test pass — the test is the smoke check, not the spec).

- [ ] **Step 3: Commit**

```powershell
git add manager
git commit -m "test: integration smoke for ContextMenuCaptureService"
```

---

### Task 14: Manual smoke test

Run the app and verify the list matches your actual right-click menu.

- [ ] **Step 1: Kill any running instance**

```powershell
Start-Process -FilePath "taskkill.exe" -ArgumentList "/F","/IM","RCMM.exe" -Verb RunAs -WindowStyle Hidden
```

- [ ] **Step 2: Build**

```powershell
dotnet build manager/RCMM.sln
```

- [ ] **Step 3: Launch**

```powershell
$exe = "manager/src/RCMM/bin/x64/Debug/net8.0-windows10.0.19041.0/RCMM.exe"
Start-Process -FilePath (Resolve-Path $exe) -Verb RunAs
```

- [ ] **Step 4: Verify**

- The list should contain entries matching what you'd actually see in your classic right-click menu (Open, Cut/Copy/Paste with disabled toggles, plus Git, VLC, WinRAR, etc.).
- Entries with no registry hide-target show the disabled toggle + "Built into Windows" label.
- Toggle a third-party verb off, click Apply, right-click a real file in Explorer, verify the entry is gone. Toggle back on, Apply, verify it's back.

Document any unexpected behavior as a follow-up task — don't patch in this iteration.

---

### Task 15: README + tag v0.2.0

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Update README status section**

Replace the existing Status block with:

```markdown
## Status

- [x] Foundation + classic-menu hide/unhide (Plan 1)
- [x] Capture-based classic menu (Plan 2) — list mirrors actual right-click menu
- [ ] Modern Win11 menu hide (Plan 3)
- [ ] Add custom items (Plan 4)
- [ ] Backup snapshot + Undo all (Plan 5)
```

- [ ] **Step 2: Commit + tag**

```powershell
git add README.md
git commit -m "docs: readme update for v0.2 capture-based classic menu"
git tag v0.2.0
```

---

## Done criteria

- All tests in `manager/test/RCMM.Tests` pass (`dotnet test`).
- `dotnet build manager/RCMM.sln` is clean.
- App launches, displays a list that mirrors the actual right-click menu for a representative set of file types.
- Toggling a third-party verb off, clicking Apply, and right-clicking the relevant file type confirms the entry disappears; toggling back restores it.
- Un-hideable items (Cut, Copy, Paste, Properties) appear in the list with a disabled toggle and a clear "Built into Windows" label.
- Tag `v0.2.0` exists on `main`.
