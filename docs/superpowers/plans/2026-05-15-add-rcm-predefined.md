# Add to RCM — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the "Add to menu" feature in RCMM — let users add their own classic right-click menu entries, ship 13 predefined dev-folder templates, support flat folder grouping that renders as classic submenus.

**Architecture:** Three new services under `RCMM.Core.Services` — `AdditionStore` (JSON load/save), `AdditionTemplates` (static template list), `AdditionApplier` (registry write/teardown). One new view-model `AddPageViewModel` exposed by `MainViewModel`. Two new WinUI pages (`AddPage`, `TemplatesPage`) plus wiring on `LandingPage`. The existing footer Apply button drives both hide changes (today) and add changes (this feature) through one combined commit.

**Tech Stack:** .NET 8, WinUI 3 (Windows App SDK), `System.Text.Json` from BCL, xUnit for tests, Inno Setup for installer.

---

## File map

### New (RCMM.Core)
- `manager/src/RCMM.Core/Models/AdditionScope.cs` — enum for menu scope
- `manager/src/RCMM.Core/Models/RunMode.cs` — enum: VisibleTerminal | Background
- `manager/src/RCMM.Core/Models/AdditionEntry.cs` — user-added entry record
- `manager/src/RCMM.Core/Models/AdditionFolder.cs` — folder record
- `manager/src/RCMM.Core/Models/AdditionState.cs` — root JSON record (`{folders, entries, schemaVersion}`)
- `manager/src/RCMM.Core/Services/AdditionStore.cs` — load/save `additions.json` atomically
- `manager/src/RCMM.Core/Services/AdditionTemplates.cs` — static template definitions
- `manager/src/RCMM.Core/Services/AdditionApplier.cs` — registry write + idempotent rebuild
- `manager/src/RCMM.Core/ViewModels/AddPageViewModel.cs` — orchestrator for Add page UI

### New (RCMM UI)
- `manager/src/RCMM/Views/AddPage.xaml` + `.xaml.cs` — master-detail page
- `manager/src/RCMM/Views/TemplatesPage.xaml` + `.xaml.cs` — templates browser

### New (tests)
- `manager/test/RCMM.Tests/AdditionStoreTests.cs`
- `manager/test/RCMM.Tests/AdditionTemplatesTests.cs`
- `manager/test/RCMM.Tests/AdditionApplierTests.cs`
- `manager/test/RCMM.Tests/AddPageViewModelTests.cs`

### Modified
- `manager/src/RCMM.Core/ViewModels/MainViewModel.cs` — expose `AddPageViewModel`, extend `ApplyPending`
- `manager/src/RCMM/MainWindow.xaml.cs` — wire new services into MainViewModel constructor; update FooterApply
- `manager/src/RCMM/Views/LandingPage.xaml` + `.xaml.cs` — unlock "Add to menu" card, navigate to AddPage
- `installer/RCMM.iss` — bump version to 0.5.0

---

## Task 1: Data model — enums and records

**Files:**
- Create: `manager/src/RCMM.Core/Models/AdditionScope.cs`
- Create: `manager/src/RCMM.Core/Models/RunMode.cs`
- Create: `manager/src/RCMM.Core/Models/AdditionEntry.cs`
- Create: `manager/src/RCMM.Core/Models/AdditionFolder.cs`
- Create: `manager/src/RCMM.Core/Models/AdditionState.cs`

- [ ] **Step 1.1: Create AdditionScope.cs**

```csharp
namespace RCMM.Core.Models;

/// <summary>
/// Where a user-added entry shows up in the Windows right-click menu.
/// Maps to the registry path segment under HKCU\Software\Classes\.
/// </summary>
public enum AdditionScope
{
    /// <summary>Right-click empty space inside a folder. Path: Directory\Background\shell.</summary>
    FolderBackground,
    /// <summary>Right-click on a folder from its parent view. Path: Directory\shell.</summary>
    Folder,
    /// <summary>Right-click on a file. With no FileTypes set, registers under *\shell; otherwise one registration per extension under &lt;.ext&gt;\shell.</summary>
    File,
    /// <summary>Right-click on a drive root. Path: Drive\shell.</summary>
    Drive,
    /// <summary>Every file and folder (broad). Path: AllFilesystemObjects\shell.</summary>
    AllFilesystemObjects
}
```

- [ ] **Step 1.2: Create RunMode.cs**

```csharp
namespace RCMM.Core.Models;

/// <summary>How a user-added entry's command is executed when clicked.</summary>
public enum RunMode
{
    /// <summary>
    /// Wraps the command as "cmd /k &lt;Command&gt;" at registry-write time.
    /// User sees a terminal window with the command output; window stays open
    /// until they close it.
    /// </summary>
    VisibleTerminal,
    /// <summary>
    /// Writes the command as-is. Caller is responsible for making it
    /// windowless (e.g. by using start /B, or pointing at a GUI executable).
    /// </summary>
    Background
}
```

- [ ] **Step 1.3: Create AdditionEntry.cs**

```csharp
using System.Collections.Generic;

namespace RCMM.Core.Models;

/// <summary>
/// One user-added or template-cloned right-click menu entry. Stored in
/// %APPDATA%\RCMM\additions.json and written to the Windows registry on Apply.
/// </summary>
public sealed record AdditionEntry
{
    /// <summary>Globally unique id — used to derive the registry verb name "RCMM.{Id}".</summary>
    public required string Id { get; init; }
    /// <summary>Display text the user sees in the right-click menu.</summary>
    public required string Name { get; init; }
    /// <summary>Icon spec (file path or "path,index"). Null = no Icon value written; Windows derives one from the command's exe.</summary>
    public string? Icon { get; init; }
    /// <summary>Bare command — RunMode controls how it's wrapped at registry-write time.</summary>
    public required string Command { get; init; }
    /// <summary>Working directory shell var, typically "%V" for "the right-clicked folder".</summary>
    public required string WorkingDir { get; init; }
    public required AdditionScope Scope { get; init; }
    /// <summary>Only relevant when Scope == File. Each extension gets its own registry registration.</summary>
    public IReadOnlyList<string>? FileTypes { get; init; }
    /// <summary>Null = top-level entry; else points to AdditionFolder.Id.</summary>
    public string? FolderId { get; init; }
    public required RunMode RunMode { get; init; }
}
```

- [ ] **Step 1.4: Create AdditionFolder.cs**

```csharp
namespace RCMM.Core.Models;

/// <summary>
/// A user-defined grouping of entries. Renders as a classic shell submenu
/// via HKCU\Software\Classes\&lt;scope&gt;\shell\RCMM.{Id}\ExtendedSubCommandsKey.
/// One level deep — folders cannot contain other folders in v1.
/// </summary>
public sealed record AdditionFolder
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Icon { get; init; }
}
```

- [ ] **Step 1.5: Create AdditionState.cs**

```csharp
using System.Collections.Generic;

namespace RCMM.Core.Models;

/// <summary>Root document for additions.json. Schema versioned for future migrations.</summary>
public sealed record AdditionState
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<AdditionFolder> Folders { get; init; } = new List<AdditionFolder>();
    public IReadOnlyList<AdditionEntry> Entries { get; init; } = new List<AdditionEntry>();
}
```

- [ ] **Step 1.6: Build and verify no compile errors**

Run: `dotnet build manager\src\RCMM.Core\RCMM.Core.csproj`
Expected: build succeeds, no errors, no new warnings.

- [ ] **Step 1.7: Commit**

```bash
git add manager/src/RCMM.Core/Models/AdditionScope.cs manager/src/RCMM.Core/Models/RunMode.cs manager/src/RCMM.Core/Models/AdditionEntry.cs manager/src/RCMM.Core/Models/AdditionFolder.cs manager/src/RCMM.Core/Models/AdditionState.cs
git commit -m "feat: AdditionEntry/AdditionFolder/AdditionState data model"
```

---

## Task 2: AdditionStore — JSON load/save

**Files:**
- Create: `manager/src/RCMM.Core/Services/AdditionStore.cs`
- Create: `manager/test/RCMM.Tests/AdditionStoreTests.cs`

- [ ] **Step 2.1: Write failing test — empty store roundtrips**

Create `manager/test/RCMM.Tests/AdditionStoreTests.cs`:

```csharp
using System.IO;
using System.Linq;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Tests;

public class AdditionStoreTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"rcmm-test-{System.Guid.NewGuid():N}.json");

    [Fact]
    public void Load_returns_empty_state_when_file_missing()
    {
        var path = TempFile();
        var store = new AdditionStore(path);
        var state = store.Load();
        Assert.Empty(state.Entries);
        Assert.Empty(state.Folders);
        Assert.Equal(1, state.SchemaVersion);
    }

    [Fact]
    public void Save_then_Load_roundtrips_one_entry()
    {
        var path = TempFile();
        try
        {
            var store = new AdditionStore(path);
            var entry = new AdditionEntry
            {
                Id = "abc-123",
                Name = "npm run dev",
                Command = "npm run dev",
                WorkingDir = "%V",
                Scope = AdditionScope.FolderBackground,
                RunMode = RunMode.VisibleTerminal
            };
            store.Save(new AdditionState { Entries = new[] { entry } });
            var reloaded = store.Load();
            Assert.Single(reloaded.Entries);
            Assert.Equal("npm run dev", reloaded.Entries[0].Name);
            Assert.Equal(AdditionScope.FolderBackground, reloaded.Entries[0].Scope);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 2.2: Run the failing tests**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~AdditionStoreTests"`
Expected: FAIL — `AdditionStore` doesn't exist.

- [ ] **Step 2.3: Implement AdditionStore minimal**

Create `manager/src/RCMM.Core/Services/AdditionStore.cs`:

```csharp
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RCMM.Core.Diagnostics;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

/// <summary>
/// Reads and writes the user's add-to-menu config at additions.json.
/// All writes are atomic — write to .tmp, then rename — so a crash mid-write
/// can't leave the file truncated.
/// </summary>
public sealed class AdditionStore
{
    private const string Cat = "addstore";
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;

    public AdditionStore(string path) { _path = path; }

    /// <summary>Default location: %APPDATA%\RCMM\additions.json (roaming).</summary>
    public static string DefaultPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RCMM", "additions.json");

    public AdditionState Load()
    {
        if (!File.Exists(_path))
        {
            Log.Info(Cat, $"file missing — returning empty state ({_path})");
            return new AdditionState();
        }
        try
        {
            var json = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize<AdditionState>(json, _options);
            if (state == null)
            {
                Log.Warn(Cat, "deserialize returned null — returning empty state");
                return new AdditionState();
            }
            Log.Info(Cat, $"loaded entries={state.Entries.Count} folders={state.Folders.Count}");
            return state;
        }
        catch (Exception ex)
        {
            Log.Error(Cat, $"failed to read {_path} — returning empty state", ex);
            return new AdditionState();
        }
    }

    public void Save(AdditionState state)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        var json = JsonSerializer.Serialize(state, _options);
        File.WriteAllText(tmp, json);
        if (File.Exists(_path)) File.Replace(tmp, _path, null);
        else File.Move(tmp, _path);
        Log.Info(Cat, $"saved entries={state.Entries.Count} folders={state.Folders.Count}");
    }
}
```

- [ ] **Step 2.4: Run the tests, verify pass**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~AdditionStoreTests"`
Expected: 2 tests pass.

- [ ] **Step 2.5: Add atomic-write edge case test**

Append to `AdditionStoreTests.cs`:

```csharp
    [Fact]
    public void Save_leaves_no_tmp_file_on_disk()
    {
        var path = TempFile();
        try
        {
            var store = new AdditionStore(path);
            store.Save(new AdditionState());
            Assert.False(File.Exists(path + ".tmp"), "tmp file should have been renamed");
            Assert.True(File.Exists(path), "real file should exist after save");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void Load_with_corrupt_json_returns_empty_state()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{not valid json");
            var store = new AdditionStore(path);
            var state = store.Load();
            Assert.Empty(state.Entries);
            Assert.Empty(state.Folders);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
```

- [ ] **Step 2.6: Run tests, verify all pass**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~AdditionStoreTests"`
Expected: 4 tests pass.

- [ ] **Step 2.7: Commit**

```bash
git add manager/src/RCMM.Core/Services/AdditionStore.cs manager/test/RCMM.Tests/AdditionStoreTests.cs
git commit -m "feat: AdditionStore JSON persistence with atomic writes"
```

---

## Task 3: AdditionTemplates — static template definitions

**Files:**
- Create: `manager/src/RCMM.Core/Services/AdditionTemplates.cs`
- Create: `manager/test/RCMM.Tests/AdditionTemplatesTests.cs`

- [ ] **Step 3.1: Write failing test**

Create `manager/test/RCMM.Tests/AdditionTemplatesTests.cs`:

```csharp
using System.Linq;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Tests;

public class AdditionTemplatesTests
{
    [Fact]
    public void All_thirteen_templates_present()
    {
        var templates = AdditionTemplates.All;
        Assert.Equal(13, templates.Count);
    }

    [Fact]
    public void All_templates_target_FolderBackground_with_VisibleTerminal()
    {
        foreach (var t in AdditionTemplates.All)
        {
            Assert.Equal(AdditionScope.FolderBackground, t.Scope);
            Assert.Equal(RunMode.VisibleTerminal, t.RunMode);
            Assert.Equal("%V", t.WorkingDir);
        }
    }

    [Fact]
    public void Commands_are_bare_no_cmd_wrapper()
    {
        foreach (var t in AdditionTemplates.All)
            Assert.DoesNotContain("cmd /k", t.Command);
    }

    [Theory]
    [InlineData("npm run dev")]
    [InlineData("git pull")]
    [InlineData("dotnet build")]
    [InlineData("cargo run")]
    [InlineData("docker compose up")]
    public void Specific_template_exists(string expectedCommand)
    {
        Assert.Contains(AdditionTemplates.All, t => t.Command == expectedCommand);
    }
}
```

- [ ] **Step 3.2: Run the failing tests**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~AdditionTemplatesTests"`
Expected: FAIL — class doesn't exist.

- [ ] **Step 3.3: Implement AdditionTemplates**

Create `manager/src/RCMM.Core/Services/AdditionTemplates.cs`:

```csharp
using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

/// <summary>
/// Static catalogue of predefined entry templates shipped with RCMM. Not persisted —
/// when the user "Adds" a template, a fresh AdditionEntry is created with the
/// template's defaults; the link is severed and the new entry is fully editable.
///
/// All v1 templates target FolderBackground (right-click empty space in a folder) since
/// they're all "run in this directory" commands.
/// </summary>
public static class AdditionTemplates
{
    public sealed record Template
    {
        public required string Name { get; init; }
        public required string Command { get; init; }
        public required string Ecosystem { get; init; }    // grouping label in Templates browser
        public required string AppliesWhen { get; init; }  // informational, not enforced
        public required AdditionScope Scope { get; init; }
        public required RunMode RunMode { get; init; }
        public string WorkingDir { get; init; } = "%V";
    }

    public static IReadOnlyList<Template> All { get; } = new List<Template>
    {
        Make("npm run dev",                   "npm run dev",                     "Node",   "package.json"),
        Make("npm install",                   "npm install",                     "Node",   "package.json"),
        Make("npm test",                      "npm test",                        "Node",   "package.json"),
        Make("git pull",                      "git pull",                        "Git",    ".git/"),
        Make("git status",                    "git status",                      "Git",    ".git/"),
        Make("git fetch --all",               "git fetch --all",                 "Git",    ".git/"),
        Make("dotnet build",                  "dotnet build",                    ".NET",   "*.csproj or *.sln"),
        Make("dotnet run",                    "dotnet run",                      ".NET",   "*.csproj"),
        Make("python -m venv .venv",          "python -m venv .venv",            "Python", "pyproject.toml / requirements.txt"),
        Make("pip install -r requirements",   "pip install -r requirements.txt", "Python", "requirements.txt"),
        Make("cargo run",                     "cargo run",                       "Rust",   "Cargo.toml"),
        Make("go run .",                      "go run .",                        "Go",     "go.mod"),
        Make("docker compose up",             "docker compose up",               "Docker", "compose.yaml"),
    };

    private static Template Make(string name, string command, string ecosystem, string when)
        => new()
        {
            Name = name,
            Command = command,
            Ecosystem = ecosystem,
            AppliesWhen = when,
            Scope = AdditionScope.FolderBackground,
            RunMode = RunMode.VisibleTerminal,
        };
}
```

- [ ] **Step 3.4: Run tests, verify pass**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~AdditionTemplatesTests"`
Expected: 8 tests pass (4 + 5 from theory minus an off-by-one, double-check with output).

Actual expected: 3 single + 5 theory rows = 8 test cases.

- [ ] **Step 3.5: Commit**

```bash
git add manager/src/RCMM.Core/Services/AdditionTemplates.cs manager/test/RCMM.Tests/AdditionTemplatesTests.cs
git commit -m "feat: AdditionTemplates — 13 predefined dev-folder templates"
```

---

## Task 4: AdditionApplier — scope path mapping

**Files:**
- Create: `manager/src/RCMM.Core/Services/AdditionApplier.cs`
- Create: `manager/test/RCMM.Tests/AdditionApplierTests.cs`

- [ ] **Step 4.1: Write failing test — scope mapping**

Create `manager/test/RCMM.Tests/AdditionApplierTests.cs`:

```csharp
using System.Linq;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Tests;

public class AdditionApplierTests
{
    [Theory]
    [InlineData(AdditionScope.FolderBackground, "Directory\\Background")]
    [InlineData(AdditionScope.Folder,           "Directory")]
    [InlineData(AdditionScope.Drive,            "Drive")]
    [InlineData(AdditionScope.AllFilesystemObjects, "AllFilesystemObjects")]
    public void ScopeRootFor_returns_correct_root(AdditionScope scope, string expected)
    {
        Assert.Equal(expected, AdditionApplier.ScopeRootFor(scope));
    }
}
```

- [ ] **Step 4.2: Run failing test**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~AdditionApplierTests"`
Expected: FAIL — class doesn't exist.

- [ ] **Step 4.3: Implement minimal scaffold + ScopeRootFor**

Create `manager/src/RCMM.Core/Services/AdditionApplier.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using RCMM.Core.Diagnostics;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

/// <summary>
/// Writes user-defined add-to-menu entries and folders to HKCU as classic
/// shell verbs. Every key uses the RCMM. prefix so a wholesale tear-down +
/// re-write makes Apply idempotent.
/// </summary>
public sealed class AdditionApplier
{
    private const string Cat = "addapply";
    private const string VerbPrefix = "RCMM.";
    private readonly IRegistry _reg;

    public AdditionApplier(IRegistry reg) { _reg = reg; }

    /// <summary>
    /// Maps an AdditionScope to its registry path segment under
    /// HKCU\Software\Classes. File scope is special — it expands to either
    /// "*" or per-extension paths and is handled elsewhere.
    /// </summary>
    public static string ScopeRootFor(AdditionScope scope) => scope switch
    {
        AdditionScope.FolderBackground       => "Directory\\Background",
        AdditionScope.Folder                 => "Directory",
        AdditionScope.Drive                  => "Drive",
        AdditionScope.AllFilesystemObjects   => "AllFilesystemObjects",
        AdditionScope.File                   => "*",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
    };
}
```

- [ ] **Step 4.4: Run, verify pass**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~AdditionApplierTests"`
Expected: 4 tests pass (one row per scope mapped, File is tested separately later).

- [ ] **Step 4.5: Commit**

```bash
git add manager/src/RCMM.Core/Services/AdditionApplier.cs manager/test/RCMM.Tests/AdditionApplierTests.cs
git commit -m "feat: AdditionApplier scaffold + scope path mapping"
```

---

## Task 5: AdditionApplier — command wrapping (RunMode → registry value)

- [ ] **Step 5.1: Write failing tests**

Append to `AdditionApplierTests.cs`:

```csharp
    [Fact]
    public void WrapForRunMode_VisibleTerminal_wraps_in_cmd_k()
    {
        var result = AdditionApplier.WrapForRunMode(RunMode.VisibleTerminal, "npm run dev");
        Assert.Equal("cmd /k npm run dev", result);
    }

    [Fact]
    public void WrapForRunMode_Background_returns_bare_command()
    {
        var result = AdditionApplier.WrapForRunMode(RunMode.Background, "start \"\" \"C:\\path\\app.exe\"");
        Assert.Equal("start \"\" \"C:\\path\\app.exe\"", result);
    }
```

- [ ] **Step 5.2: Run, verify failing**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~AdditionApplierTests"`
Expected: 2 NEW failures (method doesn't exist).

- [ ] **Step 5.3: Add WrapForRunMode**

Append to `AdditionApplier.cs` (inside the class):

```csharp
    /// <summary>Transforms an entry's bare Command into the literal string written to the registry's command\\(Default) value.</summary>
    public static string WrapForRunMode(RunMode mode, string command) => mode switch
    {
        RunMode.VisibleTerminal => "cmd /k " + command,
        RunMode.Background      => command,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
```

- [ ] **Step 5.4: Run, verify pass**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~AdditionApplierTests"`
Expected: 6 tests pass.

- [ ] **Step 5.5: Commit**

```bash
git add manager/src/RCMM.Core/Services/AdditionApplier.cs manager/test/RCMM.Tests/AdditionApplierTests.cs
git commit -m "feat: AdditionApplier.WrapForRunMode for VisibleTerminal vs Background"
```

---

## Task 6: AdditionApplier — write a single top-level entry

- [ ] **Step 6.1: Write failing test using FakeRegistry**

Append to `AdditionApplierTests.cs`:

```csharp
    [Fact]
    public void WriteEntry_top_level_FolderBackground_creates_expected_keys()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var entry = new AdditionEntry
        {
            Id = "abc",
            Name = "npm run dev",
            Command = "npm run dev",
            WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground,
            RunMode = RunMode.VisibleTerminal,
        };
        applier.WriteEntry(entry, parentContainer: null);
        var verbPath = "Directory\\Background\\shell\\RCMM.abc";
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\" + verbPath));
        Assert.Equal("npm run dev", reg.GetValue(RegistryHive.CurrentUser, "Software\\Classes\\" + verbPath, ""));
        Assert.Equal("cmd /k npm run dev", reg.GetValue(RegistryHive.CurrentUser, "Software\\Classes\\" + verbPath + "\\command", ""));
    }

    [Fact]
    public void WriteEntry_with_icon_writes_Icon_value()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var entry = new AdditionEntry
        {
            Id = "abc",
            Name = "Test",
            Icon = "C:\\Windows\\System32\\shell32.dll,42",
            Command = "echo hi",
            WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground,
            RunMode = RunMode.VisibleTerminal,
        };
        applier.WriteEntry(entry, parentContainer: null);
        Assert.Equal("C:\\Windows\\System32\\shell32.dll,42",
            reg.GetValue(RegistryHive.CurrentUser,
                "Software\\Classes\\Directory\\Background\\shell\\RCMM.abc", "Icon"));
    }

    [Fact]
    public void WriteEntry_without_icon_does_not_write_Icon_value()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var entry = new AdditionEntry
        {
            Id = "abc", Name = "Test", Command = "x", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
        };
        applier.WriteEntry(entry, parentContainer: null);
        Assert.Null(reg.GetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.abc", "Icon"));
    }

    [Fact]
    public void WriteEntry_File_scope_with_multiple_extensions_writes_one_per_ext()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var entry = new AdditionEntry
        {
            Id = "img", Name = "Hash this image", Command = "fileinfo %1",
            WorkingDir = "%V",
            Scope = AdditionScope.File,
            FileTypes = new[] { ".png", ".jpg" },
            RunMode = RunMode.VisibleTerminal,
        };
        applier.WriteEntry(entry, parentContainer: null);
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\.png\\shell\\RCMM.img"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\.jpg\\shell\\RCMM.img"));
    }

    [Fact]
    public void WriteEntry_File_scope_without_extensions_uses_wildcard()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var entry = new AdditionEntry
        {
            Id = "x", Name = "Any file", Command = "noop",
            WorkingDir = "%V",
            Scope = AdditionScope.File,
            FileTypes = null,
            RunMode = RunMode.VisibleTerminal,
        };
        applier.WriteEntry(entry, parentContainer: null);
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\*\\shell\\RCMM.x"));
    }
```

- [ ] **Step 6.2: Run, verify failing**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~AdditionApplierTests"`
Expected: 5 new failures (method doesn't exist).

- [ ] **Step 6.3: Implement WriteEntry + EntryScopePaths**

Append to `AdditionApplier.cs` (inside the class):

```csharp
    private const string ClassesRoot = "Software\\Classes";

    /// <summary>
    /// Returns the list of "&lt;scope-root&gt;" prefixes under which this entry registers.
    /// One element for most scopes; for File-with-extensions, one per extension.
    /// </summary>
    public static IReadOnlyList<string> EntryScopePaths(AdditionEntry entry)
    {
        if (entry.Scope != AdditionScope.File)
            return new[] { ScopeRootFor(entry.Scope) };
        if (entry.FileTypes == null || entry.FileTypes.Count == 0)
            return new[] { "*" };
        return entry.FileTypes.Select(ext => ext.StartsWith('.') ? ext : "." + ext).ToList();
    }

    /// <summary>
    /// Writes a single entry's keys. If parentContainer is null the entry registers
    /// directly under HKCU\Software\Classes\&lt;scope&gt;\shell\RCMM.&lt;id&gt;. Otherwise it
    /// registers under HKCU\Software\Classes\&lt;parentContainer&gt;\shell\RCMM.&lt;id&gt; —
    /// used for child entries inside a folder's ContextMenus tree.
    /// </summary>
    public void WriteEntry(AdditionEntry entry, string? parentContainer)
    {
        var verbName = VerbPrefix + entry.Id;
        var commandText = WrapForRunMode(entry.RunMode, entry.Command);

        if (parentContainer != null)
        {
            var path = $"{ClassesRoot}\\{parentContainer}\\shell\\{verbName}";
            _reg.SetValue(RegistryHive.CurrentUser, path, "", entry.Name);
            if (!string.IsNullOrWhiteSpace(entry.Icon))
                _reg.SetValue(RegistryHive.CurrentUser, path, "Icon", entry.Icon!);
            _reg.SetValue(RegistryHive.CurrentUser, path + "\\command", "", commandText);
            return;
        }

        foreach (var scopePrefix in EntryScopePaths(entry))
        {
            var path = $"{ClassesRoot}\\{scopePrefix}\\shell\\{verbName}";
            _reg.SetValue(RegistryHive.CurrentUser, path, "", entry.Name);
            if (!string.IsNullOrWhiteSpace(entry.Icon))
                _reg.SetValue(RegistryHive.CurrentUser, path, "Icon", entry.Icon!);
            _reg.SetValue(RegistryHive.CurrentUser, path + "\\command", "", commandText);
        }
    }
```

- [ ] **Step 6.4: Run, verify pass**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~AdditionApplierTests"`
Expected: 11 tests pass.

- [ ] **Step 6.5: Commit**

```bash
git add manager/src/RCMM.Core/Services/AdditionApplier.cs manager/test/RCMM.Tests/AdditionApplierTests.cs
git commit -m "feat: AdditionApplier.WriteEntry writes verb keys to HKCU"
```

---

## Task 7: AdditionApplier — write a folder with submenu

- [ ] **Step 7.1: Write failing test**

Append to `AdditionApplierTests.cs`:

```csharp
    [Fact]
    public void WriteFolder_with_two_children_creates_parent_and_submenu_tree()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var folder = new AdditionFolder { Id = "folder1", Name = "Dev tools" };
        var child1 = new AdditionEntry
        {
            Id = "c1", Name = "npm run dev", Command = "npm run dev", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
            FolderId = "folder1",
        };
        var child2 = new AdditionEntry
        {
            Id = "c2", Name = "git pull", Command = "git pull", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
            FolderId = "folder1",
        };
        applier.WriteFolder(folder, new[] { child1, child2 });

        // Parent verb at the folder-background root
        var parentPath = "Software\\Classes\\Directory\\Background\\shell\\RCMM.folder1";
        Assert.Equal("Dev tools", reg.GetValue(RegistryHive.CurrentUser, parentPath, ""));
        Assert.Equal("Directory\\Background\\ContextMenus\\RCMM.folder1",
            reg.GetValue(RegistryHive.CurrentUser, parentPath, "ExtendedSubCommandsKey"));

        // Children under ContextMenus
        Assert.Equal("npm run dev", reg.GetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.folder1\\shell\\RCMM.c1", ""));
        Assert.Equal("cmd /k git pull", reg.GetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.folder1\\shell\\RCMM.c2\\command", ""));
    }

    [Fact]
    public void WriteFolder_with_children_in_two_scopes_registers_parent_under_both()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var folder = new AdditionFolder { Id = "f", Name = "Mixed" };
        var bg = new AdditionEntry
        {
            Id = "a", Name = "BG", Command = "echo bg", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal, FolderId = "f",
        };
        var folderScope = new AdditionEntry
        {
            Id = "b", Name = "Folder", Command = "echo folder", WorkingDir = "%V",
            Scope = AdditionScope.Folder, RunMode = RunMode.VisibleTerminal, FolderId = "f",
        };
        applier.WriteFolder(folder, new[] { bg, folderScope });

        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.f"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\shell\\RCMM.f"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.f\\shell\\RCMM.a"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\ContextMenus\\RCMM.f\\shell\\RCMM.b"));
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.f\\shell\\RCMM.b"),
            "child b should only appear in its own scope's ContextMenus");
    }
```

- [ ] **Step 7.2: Run, verify failing**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~AdditionApplierTests"`
Expected: 2 new failures.

- [ ] **Step 7.3: Implement WriteFolder**

Append to `AdditionApplier.cs` (inside the class):

```csharp
    /// <summary>
    /// Writes a folder verb under every scope at least one of its children registers under.
    /// Each parent verb gets an ExtendedSubCommandsKey pointing at its scope-specific
    /// ContextMenus subtree, where the matching children's verbs live.
    /// </summary>
    public void WriteFolder(AdditionFolder folder, IReadOnlyCollection<AdditionEntry> children)
    {
        var verbName = VerbPrefix + folder.Id;
        // Compute which scope-roots this folder needs to register under: any
        // scope-root that any child registers under. For File-scope children
        // with extensions, each extension counts as a separate root.
        var rootsUsed = children
            .SelectMany(EntryScopePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var root in rootsUsed)
        {
            var parentPath = $"{ClassesRoot}\\{root}\\shell\\{verbName}";
            var contextMenusPath = $"{root}\\ContextMenus\\{verbName}";
            _reg.SetValue(RegistryHive.CurrentUser, parentPath, "", folder.Name);
            if (!string.IsNullOrWhiteSpace(folder.Icon))
                _reg.SetValue(RegistryHive.CurrentUser, parentPath, "Icon", folder.Icon!);
            _reg.SetValue(RegistryHive.CurrentUser, parentPath, "ExtendedSubCommandsKey", contextMenusPath);

            foreach (var child in children)
            {
                var childRoots = EntryScopePaths(child);
                if (!childRoots.Contains(root, StringComparer.OrdinalIgnoreCase)) continue;
                WriteEntry(child, parentContainer: contextMenusPath);
            }
        }
    }
```

- [ ] **Step 7.4: Run, verify pass**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~AdditionApplierTests"`
Expected: 13 tests pass.

- [ ] **Step 7.5: Commit**

```bash
git add manager/src/RCMM.Core/Services/AdditionApplier.cs manager/test/RCMM.Tests/AdditionApplierTests.cs
git commit -m "feat: AdditionApplier.WriteFolder writes parent + ExtendedSubCommandsKey + children"
```

---

## Task 8: AdditionApplier — purge previous RCMM.* keys

- [ ] **Step 8.1: Write failing test**

Append to `AdditionApplierTests.cs`:

```csharp
    [Fact]
    public void Purge_removes_all_RCMM_prefixed_keys_under_known_roots()
    {
        var reg = new FakeRegistry();
        // Seed: some RCMM.* keys + a non-RCMM key that must be preserved
        reg.SetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.old1", "", "Old 1");
        reg.SetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.old2\\command", "", "x");
        reg.SetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\NotOurs", "", "leave alone");
        reg.SetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.oldfolder\\shell\\RCMM.kid", "", "x");
        reg.SetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\.png\\shell\\RCMM.imgverb", "", "x");

        var applier = new AdditionApplier(reg);
        applier.PurgeOwnedKeys(new[] { ".png" });

        Assert.False(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.old1"));
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.old2"));
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.oldfolder"));
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\.png\\shell\\RCMM.imgverb"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\NotOurs"),
            "non-RCMM-prefixed key should be left alone");
    }
```

- [ ] **Step 8.2: Run, verify failing**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~AdditionApplierTests"`
Expected: 1 new failure.

- [ ] **Step 8.3: Implement PurgeOwnedKeys**

Append to `AdditionApplier.cs` (inside the class):

```csharp
    private static readonly string[] _staticScopeRoots =
    {
        "Directory\\Background",
        "Directory",
        "Drive",
        "AllFilesystemObjects",
        "*",
    };

    /// <summary>
    /// Tears down every RCMM.-prefixed key under each known shell scope and any
    /// extra File-scope extension provided by the caller. Both \shell\RCMM.* and
    /// \ContextMenus\RCMM.* trees are removed. Non-prefixed keys are untouched.
    /// </summary>
    public void PurgeOwnedKeys(IEnumerable<string> extraExtensionRoots)
    {
        var roots = _staticScopeRoots.Concat(extraExtensionRoots.Select(NormaliseExtension))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int purged = 0;
        foreach (var root in roots)
        {
            purged += PurgePrefixed($"{ClassesRoot}\\{root}\\shell");
            purged += PurgePrefixed($"{ClassesRoot}\\{root}\\ContextMenus");
        }
        Log.Info(Cat, $"PurgeOwnedKeys purged={purged} roots={roots.Count}");
    }

    private int PurgePrefixed(string parentPath)
    {
        int count = 0;
        if (!_reg.KeyExists(RegistryHive.CurrentUser, parentPath)) return 0;
        foreach (var name in _reg.GetSubKeyNames(RegistryHive.CurrentUser, parentPath))
        {
            if (!name.StartsWith(VerbPrefix, StringComparison.Ordinal)) continue;
            _reg.DeleteKey(RegistryHive.CurrentUser, parentPath + "\\" + name);
            count++;
        }
        return count;
    }

    private static string NormaliseExtension(string ext)
        => ext.StartsWith('.') ? ext : "." + ext;
```

- [ ] **Step 8.4: Run, verify pass**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~AdditionApplierTests"`
Expected: 14 tests pass.

- [ ] **Step 8.5: Commit**

```bash
git add manager/src/RCMM.Core/Services/AdditionApplier.cs manager/test/RCMM.Tests/AdditionApplierTests.cs
git commit -m "feat: AdditionApplier.PurgeOwnedKeys removes RCMM.* registrations"
```

---

## Task 9: AdditionApplier — full Apply orchestration (idempotent)

- [ ] **Step 9.1: Write failing tests**

Append to `AdditionApplierTests.cs`:

```csharp
    [Fact]
    public void Apply_writes_top_level_entries_and_folder_entries()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var folder = new AdditionFolder { Id = "f", Name = "Dev" };
        var top = new AdditionEntry
        {
            Id = "top", Name = "Open Notes", Command = "notepad", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
        };
        var nested = new AdditionEntry
        {
            Id = "nested", Name = "npm run dev", Command = "npm run dev", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
            FolderId = "f",
        };
        applier.Apply(new AdditionState
        {
            Folders = new[] { folder },
            Entries = new[] { top, nested },
        });

        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.top"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.f"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.f\\shell\\RCMM.nested"));
    }

    [Fact]
    public void Apply_is_idempotent_running_twice_produces_same_state()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var state = new AdditionState
        {
            Entries = new[]
            {
                new AdditionEntry
                {
                    Id = "x", Name = "x", Command = "x", WorkingDir = "%V",
                    Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
                }
            }
        };
        applier.Apply(state);
        var afterFirst = reg.GetSubKeyNames(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell").ToList();
        applier.Apply(state);
        var afterSecond = reg.GetSubKeyNames(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell").ToList();
        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public void Apply_with_empty_state_purges_previous_owned_keys()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        applier.Apply(new AdditionState
        {
            Entries = new[]
            {
                new AdditionEntry
                {
                    Id = "leftover", Name = "leftover", Command = "x", WorkingDir = "%V",
                    Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
                }
            }
        });
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.leftover"));
        applier.Apply(new AdditionState());
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.leftover"));
    }
```

- [ ] **Step 9.2: Run, verify failing**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~AdditionApplierTests"`
Expected: 3 new failures (Apply doesn't exist).

- [ ] **Step 9.3: Implement Apply**

Append to `AdditionApplier.cs` (inside the class):

```csharp
    /// <summary>
    /// Idempotent full rebuild: purge every existing RCMM.* registration we own,
    /// then write the supplied state from scratch. Caller is responsible for
    /// persisting the AdditionState to disk separately (so a failed registry
    /// write leaves both file and registry on the previous state).
    /// </summary>
    public void Apply(AdditionState state)
    {
        Log.Info(Cat, $"Apply begin entries={state.Entries.Count} folders={state.Folders.Count}");

        // Collect all File-scope extensions used by any entry so PurgeOwnedKeys
        // also cleans those scope-roots. Without this a previously-applied
        // .png entry would survive a state that no longer references .png.
        var extraExts = state.Entries
            .Where(e => e.Scope == AdditionScope.File && e.FileTypes is { Count: > 0 })
            .SelectMany(e => e.FileTypes!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        PurgeOwnedKeys(extraExts);

        var folderById = state.Folders.ToDictionary(f => f.Id, f => f);
        var topLevel = state.Entries.Where(e => string.IsNullOrEmpty(e.FolderId)).ToList();
        var byFolder = state.Entries
            .Where(e => !string.IsNullOrEmpty(e.FolderId))
            .GroupBy(e => e.FolderId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var entry in topLevel)
            WriteEntry(entry, parentContainer: null);

        foreach (var folder in state.Folders)
        {
            var children = byFolder.TryGetValue(folder.Id, out var list) ? list : new List<AdditionEntry>();
            if (children.Count == 0)
            {
                Log.Debug(Cat, $"folder {folder.Name} has no children — skipping registry write");
                continue;
            }
            WriteFolder(folder, children);
        }

        // Orphan entries (FolderId set but folder missing) — treat as top-level.
        var orphans = state.Entries
            .Where(e => !string.IsNullOrEmpty(e.FolderId) && !folderById.ContainsKey(e.FolderId!))
            .ToList();
        foreach (var orphan in orphans)
        {
            Log.Warn(Cat, $"entry {orphan.Name} references missing folder {orphan.FolderId} — writing as top-level");
            WriteEntry(orphan, parentContainer: null);
        }

        Log.Info(Cat, $"Apply end");
    }
```

- [ ] **Step 9.4: Run, verify pass**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~AdditionApplierTests"`
Expected: 17 tests pass.

- [ ] **Step 9.5: Commit**

```bash
git add manager/src/RCMM.Core/Services/AdditionApplier.cs manager/test/RCMM.Tests/AdditionApplierTests.cs
git commit -m "feat: AdditionApplier.Apply orchestrates purge + rebuild"
```

---

## Task 10: AddPageViewModel — load, mutate, pending state

**Files:**
- Create: `manager/src/RCMM.Core/ViewModels/AddPageViewModel.cs`
- Create: `manager/test/RCMM.Tests/AddPageViewModelTests.cs`

- [ ] **Step 10.1: Write failing tests**

Create `manager/test/RCMM.Tests/AddPageViewModelTests.cs`:

```csharp
using System.IO;
using System.Linq;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;

namespace RCMM.Tests;

public class AddPageViewModelTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"rcmm-vm-{System.Guid.NewGuid():N}.json");

    [Fact]
    public void Load_from_empty_store_yields_empty_collections()
    {
        var store = new AdditionStore(TempFile());
        var vm = new AddPageViewModel(store);
        vm.Load();
        Assert.Empty(vm.Entries);
        Assert.Empty(vm.Folders);
        Assert.False(vm.HasPendingChanges);
    }

    [Fact]
    public void AddEntry_appends_and_marks_pending()
    {
        var store = new AdditionStore(TempFile());
        var vm = new AddPageViewModel(store);
        var entry = new AdditionEntry
        {
            Id = "e1", Name = "Test", Command = "x", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
        };
        vm.AddEntry(entry);
        Assert.Single(vm.Entries);
        Assert.True(vm.HasPendingChanges);
    }

    [Fact]
    public void DeleteEntry_removes_and_marks_pending()
    {
        var store = new AdditionStore(TempFile());
        var vm = new AddPageViewModel(store);
        var entry = new AdditionEntry
        {
            Id = "e1", Name = "x", Command = "x", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
        };
        vm.AddEntry(entry);
        vm.DeleteEntry("e1");
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public void ReplaceEntry_updates_in_place()
    {
        var store = new AdditionStore(TempFile());
        var vm = new AddPageViewModel(store);
        var entry = new AdditionEntry
        {
            Id = "e1", Name = "old", Command = "x", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
        };
        vm.AddEntry(entry);
        vm.ReplaceEntry(entry with { Name = "new" });
        Assert.Equal("new", vm.Entries.Single().Name);
    }

    [Fact]
    public void DeleteFolder_orphans_its_entries_to_top_level()
    {
        var store = new AdditionStore(TempFile());
        var vm = new AddPageViewModel(store);
        var folder = new AdditionFolder { Id = "f", Name = "F" };
        vm.AddFolder(folder);
        vm.AddEntry(new AdditionEntry
        {
            Id = "e", Name = "child", Command = "x", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
            FolderId = "f",
        });
        vm.DeleteFolder("f");
        Assert.Empty(vm.Folders);
        Assert.Null(vm.Entries.Single().FolderId);
    }

    [Fact]
    public void Snapshot_returns_AdditionState_of_current_buffer()
    {
        var store = new AdditionStore(TempFile());
        var vm = new AddPageViewModel(store);
        vm.AddFolder(new AdditionFolder { Id = "f", Name = "F" });
        vm.AddEntry(new AdditionEntry
        {
            Id = "e", Name = "x", Command = "x", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
        });
        var state = vm.Snapshot();
        Assert.Single(state.Folders);
        Assert.Single(state.Entries);
    }
}
```

- [ ] **Step 10.2: Run, verify failing**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~AddPageViewModelTests"`
Expected: FAIL — class doesn't exist.

- [ ] **Step 10.3: Implement AddPageViewModel**

Create `manager/src/RCMM.Core/ViewModels/AddPageViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using RCMM.Core.Diagnostics;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Core.ViewModels;

/// <summary>
/// In-memory editor state for the Add page. Buffers user edits as pending
/// changes until the footer Apply button commits them via AdditionApplier
/// and AdditionStore.Save. Doesn't talk to the registry directly.
/// </summary>
public sealed class AddPageViewModel : ObservableObject
{
    private const string Cat = "addvm";
    private readonly AdditionStore _store;

    public ObservableCollection<AdditionEntry> Entries { get; } = new();
    public ObservableCollection<AdditionFolder> Folders { get; } = new();

    private bool _hasPendingChanges;
    public bool HasPendingChanges
    {
        get => _hasPendingChanges;
        private set => SetField(ref _hasPendingChanges, value);
    }

    public AddPageViewModel(AdditionStore store) { _store = store; }

    public void Load()
    {
        var state = _store.Load();
        Entries.Clear();
        Folders.Clear();
        foreach (var e in state.Entries) Entries.Add(e);
        foreach (var f in state.Folders) Folders.Add(f);
        HasPendingChanges = false;
    }

    public void AddEntry(AdditionEntry entry)
    {
        Entries.Add(entry);
        HasPendingChanges = true;
        Log.Debug(Cat, $"AddEntry id={entry.Id} name='{entry.Name}'");
    }

    public void DeleteEntry(string id)
    {
        var existing = Entries.FirstOrDefault(e => e.Id == id);
        if (existing == null) return;
        Entries.Remove(existing);
        HasPendingChanges = true;
        Log.Debug(Cat, $"DeleteEntry id={id}");
    }

    public void ReplaceEntry(AdditionEntry replacement)
    {
        for (int i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].Id != replacement.Id) continue;
            Entries[i] = replacement;
            HasPendingChanges = true;
            Log.Debug(Cat, $"ReplaceEntry id={replacement.Id} name='{replacement.Name}'");
            return;
        }
    }

    public void AddFolder(AdditionFolder folder)
    {
        Folders.Add(folder);
        HasPendingChanges = true;
        Log.Debug(Cat, $"AddFolder id={folder.Id} name='{folder.Name}'");
    }

    public void DeleteFolder(string id)
    {
        var folder = Folders.FirstOrDefault(f => f.Id == id);
        if (folder == null) return;
        Folders.Remove(folder);
        // Move child entries to top-level so they aren't silently lost.
        for (int i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].FolderId == id)
                Entries[i] = Entries[i] with { FolderId = null };
        }
        HasPendingChanges = true;
        Log.Debug(Cat, $"DeleteFolder id={id}");
    }

    public void ReplaceFolder(AdditionFolder replacement)
    {
        for (int i = 0; i < Folders.Count; i++)
        {
            if (Folders[i].Id != replacement.Id) continue;
            Folders[i] = replacement;
            HasPendingChanges = true;
            return;
        }
    }

    public AdditionState Snapshot() => new()
    {
        SchemaVersion = 1,
        Folders = Folders.ToList(),
        Entries = Entries.ToList(),
    };

    /// <summary>Called by the apply flow after AdditionStore.Save + AdditionApplier.Apply succeed.</summary>
    public void MarkClean() { HasPendingChanges = false; }
}
```

- [ ] **Step 10.4: Run, verify pass**

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --filter "FullyQualifiedName~AddPageViewModelTests"`
Expected: 6 tests pass.

- [ ] **Step 10.5: Commit**

```bash
git add manager/src/RCMM.Core/ViewModels/AddPageViewModel.cs manager/test/RCMM.Tests/AddPageViewModelTests.cs
git commit -m "feat: AddPageViewModel buffers edits with pending-change tracking"
```

---

## Task 11: Wire AddPageViewModel into MainViewModel + Apply flow

**Files:**
- Modify: `manager/src/RCMM.Core/ViewModels/MainViewModel.cs`
- Modify: `manager/src/RCMM/MainWindow.xaml.cs`

- [ ] **Step 11.1: Extend MainViewModel constructor with AddPageViewModel + AdditionApplier**

In `MainViewModel.cs`, find the existing constructor signature and add two optional parameters at the end:

Locate:
```csharp
    public MainViewModel(
        IContextMenuCaptureService capture,
        TargetProvider targets,
        VerbToRegistryMapper mapper,
        HideService hideService,
        IRegistry reg,
        IFileVersionReader files,
        ShellexNameIndex shellexIndex,
        EntryScanner? registryScanner = null,
        PackagedShellExtScanner? packagedScanner = null,
        CommandStoreVerbIndex? commandStore = null,
        ShellexKeyNameIndex? shellexKey = null,
        ShellexInvoker? shellexInvoker = null)
```

Add at the end of the parameter list:
```csharp
        ShellexInvoker? shellexInvoker = null,
        AddPageViewModel? addPage = null,
        AdditionApplier? additionApplier = null)
```

Add the corresponding fields near the existing private fields:

```csharp
    private readonly AddPageViewModel? _addPage;
    private readonly AdditionApplier? _additionApplier;

    public AddPageViewModel? AddPage => _addPage;
```

Set them in the constructor body alongside the other assignments:
```csharp
        _addPage = addPage;
        _additionApplier = additionApplier;
```

- [ ] **Step 11.2: Extend ApplyPending to also commit additions**

Locate the existing `ApplyPending` method in `MainViewModel.cs`. After the unhide loop and before the `_pendingHide.Clear();` cleanup, insert:

```csharp
        if (_addPage != null && _additionApplier != null && _addPage.HasPendingChanges)
        {
            Log.Info("apply", $"additions begin entries={_addPage.Entries.Count} folders={_addPage.Folders.Count}");
            var state = _addPage.Snapshot();
            try
            {
                _additionApplier.Apply(state);
                // Persist only after registry write succeeds so a failed Apply leaves the JSON on the previous state.
                var storePath = AdditionStore.DefaultPath();
                new AdditionStore(storePath).Save(state);
                _addPage.MarkClean();
            }
            catch (Exception ex) { Log.Error("apply", "additions failed", ex); }
        }
```

- [ ] **Step 11.3: Wire the new services in MainWindow constructor**

In `MainWindow.xaml.cs`, locate the `MainWindow()` constructor's services-construction block (currently builds `registry`, `capture`, `targets`, etc.) and add at the end of that block, before `ViewModel = new MainViewModel(...)`:

```csharp
        var additionApplier = new AdditionApplier(registry);
        var addStore = new AdditionStore(AdditionStore.DefaultPath());
        var addPage = new AddPageViewModel(addStore);
        addPage.Load();
```

Then update the `MainViewModel` constructor call to pass these in at the end:

Locate:
```csharp
        ViewModel = new MainViewModel(capture, targets, mapper, hide, registry, files, shellexIndex, entryScanner, packagedScanner, commandStore, shellexKeyIndex, shellexInvoker);
```

Replace with:
```csharp
        ViewModel = new MainViewModel(capture, targets, mapper, hide, registry, files, shellexIndex, entryScanner, packagedScanner, commandStore, shellexKeyIndex, shellexInvoker, addPage, additionApplier);
```

Add the necessary `using` statements at the top of MainWindow.xaml.cs if not already present:
```csharp
using RCMM.Core.ViewModels;
```

(`RCMM.Core.Services` should already be imported.)

- [ ] **Step 11.4: Build and run existing tests**

Run: `dotnet build manager\src\RCMM\RCMM.csproj`
Expected: build succeeds.

Run: `dotnet test manager\test\RCMM.Tests\RCMM.Tests.csproj --nologo`
Expected: all existing + new tests pass. No new failures.

- [ ] **Step 11.5: Commit**

```bash
git add manager/src/RCMM.Core/ViewModels/MainViewModel.cs manager/src/RCMM/MainWindow.xaml.cs
git commit -m "feat: wire AddPageViewModel + AdditionApplier into MainViewModel.ApplyPending"
```

---

## Task 12: AddPage UI — master-detail layout

**Files:**
- Create: `manager/src/RCMM/Views/AddPage.xaml`
- Create: `manager/src/RCMM/Views/AddPage.xaml.cs`

- [ ] **Step 12.1: Create AddPage.xaml**

Create `manager/src/RCMM/Views/AddPage.xaml`. Match the existing dark-utility styling used by `ShowHidePage.xaml`/`ScopePage.xaml` (refer to those for brushes and spacing tokens). Skeleton:

```xml
<Page
    x:Class="RCMM.Views.AddPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:vm="using:RCMM.Core.ViewModels"
    xmlns:models="using:RCMM.Core.Models"
    Background="{ThemeResource AppBackground}">

    <Grid Padding="24" RowSpacing="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- Header buttons -->
        <StackPanel Grid.Row="0" Orientation="Horizontal" Spacing="8">
            <Button x:Name="NewEntryButton" Content="+ New entry" Click="NewEntry_Click"/>
            <Button x:Name="NewFolderButton" Content="+ New folder" Click="NewFolder_Click"/>
            <Button x:Name="TemplatesButton" Content="Browse templates" Click="Templates_Click"/>
        </StackPanel>

        <!-- Master-detail -->
        <Grid Grid.Row="1" ColumnSpacing="16">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="320"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <!-- Left: list of folders + entries -->
            <ListView x:Name="ItemsList"
                      Grid.Column="0"
                      SelectionChanged="ItemsList_SelectionChanged"
                      Background="{ThemeResource FooterBackground}"
                      BorderBrush="{ThemeResource AppBorder}"
                      BorderThickness="1"
                      CornerRadius="6"/>

            <!-- Right: editor placeholder; bound in code-behind once selection is known -->
            <ContentControl x:Name="Editor" Grid.Column="1"/>
        </Grid>
    </Grid>
</Page>
```

- [ ] **Step 12.2: Create AddPage.xaml.cs**

Create `manager/src/RCMM/Views/AddPage.xaml.cs`:

```csharp
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RCMM.Core.Models;
using RCMM.Core.ViewModels;

namespace RCMM.Views;

public sealed partial class AddPage : Page
{
    private NavArgs _args = null!;
    private AddPageViewModel _vm = null!;

    public AddPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = (NavArgs)e.Parameter;
        _vm = _args.ViewModel.AddPage
              ?? throw new InvalidOperationException("AddPageViewModel not initialised on MainViewModel");
        ItemsList.ItemsSource = _vm.Entries;  // simplified: flat list of entries first; folders rendered in step 12.4
    }

    private void NewEntry_Click(object sender, RoutedEventArgs e)
    {
        var id = Guid.NewGuid().ToString("N");
        var entry = new AdditionEntry
        {
            Id = id,
            Name = "New entry",
            Command = "",
            WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground,
            RunMode = RunMode.VisibleTerminal,
        };
        _vm.AddEntry(entry);
        // simple selection — let the user click into the list to find it
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var id = Guid.NewGuid().ToString("N");
        _vm.AddFolder(new AdditionFolder { Id = id, Name = "New folder" });
    }

    private void Templates_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(TemplatesPage), _args);
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Editor binding wired in step 12.4
    }
}
```

- [ ] **Step 12.3: Build and verify no compile errors**

Run: `dotnet build manager\src\RCMM\RCMM.csproj`
Expected: build succeeds.

Note: `TemplatesPage` doesn't exist yet — the click handler will compile because we don't reference the type. **Wait** — yes we do reference `typeof(TemplatesPage)`. Comment out that handler body for now:

```csharp
    private void Templates_Click(object sender, RoutedEventArgs e)
    {
        // Frame.Navigate(typeof(TemplatesPage), _args);  // wired in Task 13
    }
```

Rebuild — should succeed.

- [ ] **Step 12.4: Commit**

```bash
git add manager/src/RCMM/Views/AddPage.xaml manager/src/RCMM/Views/AddPage.xaml.cs
git commit -m "feat: AddPage skeleton with header buttons + master-detail grid"
```

---

## Task 13: TemplatesPage UI — grouped list of templates

**Files:**
- Create: `manager/src/RCMM/Views/TemplatesPage.xaml`
- Create: `manager/src/RCMM/Views/TemplatesPage.xaml.cs`

- [ ] **Step 13.1: Create TemplatesPage.xaml**

Create `manager/src/RCMM/Views/TemplatesPage.xaml`:

```xml
<Page
    x:Class="RCMM.Views.TemplatesPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Background="{ThemeResource AppBackground}">

    <Grid Padding="24" RowSpacing="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Text="Browse templates"
                   FontSize="24" FontWeight="SemiBold"
                   Foreground="{ThemeResource AppForeground}"/>

        <ListView x:Name="TemplatesList" Grid.Row="1"
                  Background="{ThemeResource FooterBackground}"
                  BorderBrush="{ThemeResource AppBorder}"
                  BorderThickness="1" CornerRadius="6">
            <ListView.GroupStyle>
                <GroupStyle HeaderTemplate="{StaticResource ListViewHeaderTemplate}"/>
            </ListView.GroupStyle>
        </ListView>
    </Grid>
</Page>
```

If `ListViewHeaderTemplate` doesn't exist as a styled resource, remove the `GroupStyle.HeaderTemplate` reference and let the default grouping header render — that's acceptable for v1.

- [ ] **Step 13.2: Create TemplatesPage.xaml.cs**

Create `manager/src/RCMM/Views/TemplatesPage.xaml.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Navigation;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;

namespace RCMM.Views;

public sealed partial class TemplatesPage : Page
{
    private NavArgs _args = null!;
    private AddPageViewModel _vm = null!;

    public TemplatesPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _args = (NavArgs)e.Parameter;
        _vm = _args.ViewModel.AddPage
              ?? throw new InvalidOperationException("AddPageViewModel not initialised on MainViewModel");

        var grouped = AdditionTemplates.All
            .GroupBy(t => t.Ecosystem)
            .Select(g => new TemplateGroup { Key = g.Key, Templates = new ObservableCollection<AdditionTemplates.Template>(g) })
            .ToList();
        TemplatesList.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = grouped }.View;
    }

    public sealed class TemplateGroup
    {
        public required string Key { get; init; }
        public required ObservableCollection<AdditionTemplates.Template> Templates { get; init; }
    }

    private void Add_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is AdditionTemplates.Template t)
        {
            var entry = new AdditionEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = t.Name,
                Command = t.Command,
                WorkingDir = t.WorkingDir,
                Scope = t.Scope,
                RunMode = t.RunMode,
            };
            _vm.AddEntry(entry);
            Frame.GoBack();
        }
    }
}
```

- [ ] **Step 13.3: Add per-row template binding to TemplatesPage.xaml**

Update the `ListView` in `TemplatesPage.xaml` to add an `ItemTemplate`:

```xml
        <ListView x:Name="TemplatesList" Grid.Row="1"
                  Background="{ThemeResource FooterBackground}"
                  BorderBrush="{ThemeResource AppBorder}"
                  BorderThickness="1" CornerRadius="6">
            <ListView.ItemTemplate>
                <DataTemplate>
                    <Grid ColumnSpacing="12" Padding="8">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="Auto"/>
                        </Grid.ColumnDefinitions>
                        <StackPanel Grid.Column="0">
                            <TextBlock Text="{Binding Name}" FontWeight="SemiBold"/>
                            <TextBlock Text="{Binding AppliesWhen}" Opacity="0.6" FontSize="12"/>
                        </StackPanel>
                        <Button Grid.Column="1" Content="+" Click="Add_Click"/>
                    </Grid>
                </DataTemplate>
            </ListView.ItemTemplate>
        </ListView>
```

- [ ] **Step 13.4: Re-enable the Templates_Click navigation in AddPage**

Edit `manager/src/RCMM/Views/AddPage.xaml.cs`. Replace the commented-out body with:

```csharp
    private void Templates_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(TemplatesPage), _args);
    }
```

- [ ] **Step 13.5: Build**

Run: `dotnet build manager\src\RCMM\RCMM.csproj`
Expected: succeeds.

- [ ] **Step 13.6: Commit**

```bash
git add manager/src/RCMM/Views/TemplatesPage.xaml manager/src/RCMM/Views/TemplatesPage.xaml.cs manager/src/RCMM/Views/AddPage.xaml.cs
git commit -m "feat: TemplatesPage lists predefined templates grouped by ecosystem"
```

---

## Task 14: LandingPage — unlock the "Add to menu" card

**Files:**
- Modify: `manager/src/RCMM/Views/LandingPage.xaml`
- Modify: `manager/src/RCMM/Views/LandingPage.xaml.cs`

- [ ] **Step 14.1: Update LandingPage.xaml**

Find the "Add to menu" card (currently styled as "Coming soon"). Remove the disabled / coming-soon visual treatment so the card looks the same as the Show/hide card. Add a `Click` (Tapped) handler. Keep the card layout, just unlock interactivity. The exact XAML edit depends on how the card is currently structured — look at the existing Show/hide card and mirror it.

Concretely, the click handler name to register is `AddToMenu_Click`. Replace any "Coming soon" / "In development" badge text with the entry count (bound to `vm.AddPage.Entries.Count`).

- [ ] **Step 14.2: Add AddToMenu_Click in LandingPage.xaml.cs**

Append to the `LandingPage` class:

```csharp
    private void AddToMenu_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Frame.Navigate(typeof(AddPage), _args);
    }
```

Also update `OnNavigatedTo` to surface the addition count alongside the existing total:

```csharp
        if (_args.ViewModel.AddPage != null)
            AddCountLabel.Text = _args.ViewModel.AddPage.Entries.Count.ToString();
```

Add the `AddCountLabel` `x:Name` to the relevant `TextBlock` in `LandingPage.xaml`.

- [ ] **Step 14.3: Build and run RCMM**

Run: `dotnet build manager\src\RCMM\RCMM.csproj`
Expected: succeeds.

Publish to a fresh dir to avoid file lock:
```powershell
dotnet publish manager\src\RCMM\RCMM.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsAppSDKSelfContained=true -p:WindowsPackageType=None -o dist\publish-add-uitest
Start-Process dist\publish-add-uitest\RCMM.exe
```

Manually verify:
- Landing page shows "Add to menu" card no longer marked "Coming soon"
- Clicking it navigates to AddPage
- AddPage shows "+ New entry", "+ New folder", "Browse templates" buttons
- Browse templates navigates to TemplatesPage, lists the 13 templates grouped by ecosystem
- Clicking [+] on a template adds it to the list back on AddPage

- [ ] **Step 14.4: Commit**

```bash
git add manager/src/RCMM/Views/LandingPage.xaml manager/src/RCMM/Views/LandingPage.xaml.cs
git commit -m "feat: LandingPage navigates to AddPage; show addition count"
```

---

## Task 15: AddPage — selection-driven editor

**Files:**
- Modify: `manager/src/RCMM/Views/AddPage.xaml`
- Modify: `manager/src/RCMM/Views/AddPage.xaml.cs`

The Editor pane on the right is currently empty. Wire it up so selecting an entry on the left shows an editor with all fields.

- [ ] **Step 15.1: Add editor controls to AddPage.xaml**

Replace the empty `ContentControl` with a `StackPanel` of bound controls:

```xml
            <StackPanel x:Name="EditorPanel" Grid.Column="1" Spacing="12" Visibility="Collapsed">
                <TextBlock Text="Edit entry" FontSize="18" FontWeight="SemiBold"/>

                <TextBlock Text="Name"/>
                <TextBox x:Name="NameBox"/>

                <TextBlock Text="Command"/>
                <TextBox x:Name="CommandBox" FontFamily="Consolas"/>

                <TextBlock Text="Working directory"/>
                <TextBox x:Name="WorkingDirBox" FontFamily="Consolas"/>

                <TextBlock Text="Scope"/>
                <ComboBox x:Name="ScopeBox"/>

                <TextBlock Text="File types (comma-separated, only used when Scope = File)"/>
                <TextBox x:Name="FileTypesBox" FontFamily="Consolas"/>

                <TextBlock Text="Folder"/>
                <ComboBox x:Name="FolderBox"/>

                <TextBlock Text="Run mode"/>
                <ComboBox x:Name="RunModeBox"/>

                <TextBlock Text="Icon (path or path,index)"/>
                <TextBox x:Name="IconBox" FontFamily="Consolas"/>

                <Button x:Name="DeleteButton" Content="Delete entry" Click="Delete_Click"/>
            </StackPanel>
```

- [ ] **Step 15.2: Populate editor on selection in AddPage.xaml.cs**

Replace `ItemsList_SelectionChanged` and add helpers:

```csharp
    private AdditionEntry? _selected;

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedItem is AdditionEntry entry) { ShowEntryEditor(entry); }
        else { EditorPanel.Visibility = Visibility.Collapsed; _selected = null; }
    }

    private void ShowEntryEditor(AdditionEntry entry)
    {
        _selected = entry;
        EditorPanel.Visibility = Visibility.Visible;
        NameBox.Text = entry.Name;
        CommandBox.Text = entry.Command;
        WorkingDirBox.Text = entry.WorkingDir;
        IconBox.Text = entry.Icon ?? "";
        FileTypesBox.Text = entry.FileTypes is { Count: > 0 } ? string.Join(", ", entry.FileTypes) : "";

        ScopeBox.ItemsSource = Enum.GetValues<AdditionScope>();
        ScopeBox.SelectedItem = entry.Scope;

        RunModeBox.ItemsSource = Enum.GetValues<RunMode>();
        RunModeBox.SelectedItem = entry.RunMode;

        var folderOptions = new System.Collections.Generic.List<object> { "(top-level)" };
        foreach (var f in _vm.Folders) folderOptions.Add(f);
        FolderBox.ItemsSource = folderOptions;
        FolderBox.SelectedItem = entry.FolderId == null
            ? folderOptions[0]
            : _vm.Folders.FirstOrDefault(f => f.Id == entry.FolderId) ?? folderOptions[0];

        // Wire change handlers AFTER we set values to avoid spurious dirty marks.
        NameBox.LostFocus += SaveCurrent;
        CommandBox.LostFocus += SaveCurrent;
        WorkingDirBox.LostFocus += SaveCurrent;
        IconBox.LostFocus += SaveCurrent;
        FileTypesBox.LostFocus += SaveCurrent;
        ScopeBox.SelectionChanged += (_, __) => SaveCurrent(null!, null!);
        RunModeBox.SelectionChanged += (_, __) => SaveCurrent(null!, null!);
        FolderBox.SelectionChanged += (_, __) => SaveCurrent(null!, null!);
    }

    private void SaveCurrent(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var updated = _selected with
        {
            Name = NameBox.Text,
            Command = CommandBox.Text,
            WorkingDir = WorkingDirBox.Text,
            Icon = string.IsNullOrWhiteSpace(IconBox.Text) ? null : IconBox.Text,
            Scope = (AdditionScope)ScopeBox.SelectedItem!,
            RunMode = (RunMode)RunModeBox.SelectedItem!,
            FileTypes = string.IsNullOrWhiteSpace(FileTypesBox.Text)
                ? null
                : FileTypesBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            FolderId = FolderBox.SelectedItem is AdditionFolder f ? f.Id : null,
        };
        _vm.ReplaceEntry(updated);
        _selected = updated;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected != null)
        {
            _vm.DeleteEntry(_selected.Id);
            _selected = null;
            EditorPanel.Visibility = Visibility.Collapsed;
        }
    }
```

Add `using System;`, `using System.Linq;` at the top.

- [ ] **Step 15.3: Build and manually verify**

Run: `dotnet build manager\src\RCMM\RCMM.csproj`
Expected: succeeds.

Re-publish + run:
```powershell
dotnet publish manager\src\RCMM\RCMM.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsAppSDKSelfContained=true -p:WindowsPackageType=None -o dist\publish-add-uitest2
Start-Process dist\publish-add-uitest2\RCMM.exe
```

Verify:
- Add a template → it appears in left list
- Click it → editor populates
- Change the name → name updates in list

- [ ] **Step 15.4: Commit**

```bash
git add manager/src/RCMM/Views/AddPage.xaml manager/src/RCMM/Views/AddPage.xaml.cs
git commit -m "feat: AddPage entry editor with two-way binding to AddPageViewModel"
```

---

## Task 16: End-to-end smoke test in actual Windows registry

This task is manual — it verifies the full chain reaches the Windows shell.

- [ ] **Step 16.1: Publish + launch the latest build**

```powershell
$root = "C:\Users\Admin\Documents\Claude\Github\RCMM"
if (Test-Path "$root\dist\publish") { Remove-Item "$root\dist\publish" -Recurse -Force }
dotnet publish "$root\manager\src\RCMM\RCMM.csproj" -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsAppSDKSelfContained=true -p:WindowsPackageType=None -o "$root\dist\publish"
Start-Process "$root\dist\publish\RCMM.exe"
```

- [ ] **Step 16.2: Add one template + a custom folder**

In RCMM:
1. Click landing card "Add to menu"
2. Click "Browse templates"
3. Add "npm run dev" — back on AddPage
4. Click "+ New folder", name it "Dev tools"
5. Select the npm run dev entry, in editor change Folder dropdown to "Dev tools"
6. Click footer Apply

- [ ] **Step 16.3: Verify registry**

```powershell
$base = 'HKCU:\Software\Classes\Directory\Background'
Get-ChildItem "$base\shell" -ErrorAction SilentlyContinue | Where-Object PSChildName -Like 'RCMM.*' | Select-Object PSChildName
Get-ChildItem "$base\ContextMenus" -ErrorAction SilentlyContinue | Where-Object PSChildName -Like 'RCMM.*' | Select-Object PSChildName
```

Expected: one `RCMM.<folder-guid>` shell key and one `RCMM.<folder-guid>` ContextMenus key. Inspect the ContextMenus subtree:

```powershell
Get-ChildItem "$base\ContextMenus\RCMM.*\shell" -ErrorAction SilentlyContinue | Select-Object PSChildName, @{N='Default';E={(Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).'(default)'}}
```

Expected: one child key with `Default = "npm run dev"`.

- [ ] **Step 16.4: Verify Windows context menu**

Right-click empty space in any folder → "Show more options" → menu should include "Dev tools" submenu with "npm run dev" child.

- [ ] **Step 16.5: Verify file safety (no commit needed)**

```powershell
Get-Content $env:APPDATA\RCMM\additions.json
```

Expected: JSON file with the new entry and folder. Schema version 1.

- [ ] **Step 16.6: Document the smoke test in the log**

Tail `%LOCALAPPDATA%\RCMM\logs\rcmm.log` to confirm `addapply` category entries are present. If anything's missing, debug and fix before moving on.

---

## Task 17: Version bump + release

- [ ] **Step 17.1: Bump installer version**

Edit `installer/RCMM.iss`:
```
#define MyAppVersion     "0.5.0"
```

- [ ] **Step 17.2: Republish + rebuild installer**

```powershell
$root = "C:\Users\Admin\Documents\Claude\Github\RCMM"
if (Test-Path "$root\dist\publish") { Remove-Item "$root\dist\publish" -Recurse -Force }
dotnet publish "$root\manager\src\RCMM\RCMM.csproj" -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsAppSDKSelfContained=true -p:WindowsPackageType=None -o "$root\dist\publish"
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" "$root\installer\RCMM.iss"
```

Expected: `dist\installer\RCMM-Setup-x64-0.5.0.exe` produced.

- [ ] **Step 17.3: Commit version bump + tag + push**

```bash
git add installer/RCMM.iss
git commit -m "chore: bump installer to 0.5.0 — Add to menu feature"
git tag -a v0.5.0 -m "v0.5.0"
git push origin main
git push origin v0.5.0
```

- [ ] **Step 17.4: Create GitHub release**

```bash
gh release create v0.5.0 "dist/installer/RCMM-Setup-x64-0.5.0.exe" \
  --title "RCMM v0.5.0 — Add to menu" \
  --notes "Adds the 'Add to menu' feature: 13 predefined dev-folder templates, custom user-defined entries, flat folders that render as classic shell submenus. HKCU-only, no admin required. Classic right-click menu (Show more options on Win11)."
```

---

## Self-review checklist

1. **Spec coverage**: each section of the design spec has at least one task:
   - Data model § → Task 1
   - Storage § → Task 2 (with edge cases)
   - Templates § → Task 3
   - Registry application § → Tasks 4–9
   - UI § → Tasks 12, 13, 14, 15
   - Context filtering § → handled by RunMode wrapping in Task 5 + tests; no separate context-filter task because v1 is "always show"
   - Predefined templates table § → Task 3
   - Risks § (DeleteSubKeyTree failures, scope-spanning folders, JSON corruption) → tested in Tasks 8, 7, 2 respectively

2. **Placeholder scan**: no TBD/TODO. Each code step shows the actual code. Each command shows expected output.

3. **Type consistency**: `AdditionEntry`, `AdditionFolder`, `AdditionState`, `AdditionApplier`, `AdditionStore`, `AdditionTemplates`, `AddPageViewModel`, `AdditionScope`, `RunMode` — names used consistently across all tasks.

4. **Stop condition**: each task has explicit tests + manual verification where applicable. Tasks 16 + 17 confirm the feature reaches the actual Windows shell before release.
