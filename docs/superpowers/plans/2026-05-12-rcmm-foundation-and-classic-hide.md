# RCMM Plan 1 — Foundation + Classic-Menu Hide/Unhide

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a usable MVP of RCMM that scans the user's classic ("Show more options") right-click menu entries across Files, Folders, Drives, and Desktop & folder background scopes, and lets the user toggle entries on/off via a hub-of-cards UI. After Apply, Explorer restarts and the menu reflects the changes.

**Architecture:** WinUI 3 / .NET 8 single-process desktop app, always elevated. All registry access goes through an `IRegistry` abstraction (in-memory fake for tests, Win32 impl for runtime). Services are stateless and operate on `ContextMenuEntry` records; the UI is a thin ViewModel layer over those services. No background service; the user clicks Apply when ready and we restart Explorer.

**Tech Stack:** .NET 8, WinUI 3 (Microsoft.WindowsAppSDK), C# 12, xUnit, System.Text.Json, Microsoft.Win32.Registry.

**Out of scope for this plan (deferred to plans 2-4):**
- Modern Windows 11 menu scanning/hiding (plan 2)
- Adding custom items (plan 3)
- Backup snapshot + Undo all + Settings dialog (plan 4)

---

## File Structure

```
RCMM/
  manager/
    RCMM.sln
    src/RCMM/
      RCMM.csproj
      app.manifest                          # requireAdministrator
      App.xaml / App.xaml.cs                # entry point, DI wire-up
      MainWindow.xaml / MainWindow.xaml.cs  # window shell, title bar, theming, footer
      Assets/AppIcon.ico                    # placeholder icon
      Models/
        Scope.cs                            # enum: Files, Folders, Drives, Background
        EntryKind.cs                        # enum: ShellVerb, ShellExtension
        ContextMenuEntry.cs                 # record type
        PendingChange.cs                    # record type
        Config.cs                           # persistence shape
      Services/
        IRegistry.cs                        # abstraction
        Win32Registry.cs                    # concrete: Microsoft.Win32.Registry wrapper
        ConfigStore.cs                      # load/save/debounce JSON
        ClsidResolver.cs                    # CLSID → DLL path → FileDescription
        ClassicVerbScanner.cs               # HKCR\<scope>\shell\*
        ClassicShellexScanner.cs            # HKCR\<scope>\shellex\ContextMenuHandlers\*
        EntryScanner.cs                     # orchestrator over both scanners across all scopes
        HideService.cs                      # LegacyDisable + HKCU shellex masking
        ExplorerRestart.cs                  # taskkill + start explorer
      ViewModels/
        ObservableObject.cs                 # INotifyPropertyChanged base
        MainViewModel.cs                    # app-level state, pending changes count
        ScopeListViewModel.cs               # one scope's entries
        EntryRowViewModel.cs                # one row
      Views/
        LandingPage.xaml / .xaml.cs         # hub of cards
        ScopePage.xaml / .xaml.cs           # drill-down list
      Util/
        IconHelper.cs                       # extract icon → BitmapImage
        Logger.cs                           # rolling file log
    test/RCMM.Tests/
      RCMM.Tests.csproj                     # xUnit, plain net8.0 (no WinUI)
      FakeRegistry.cs                       # in-memory IRegistry
      ConfigStoreTests.cs
      ClsidResolverTests.cs
      ClassicVerbScannerTests.cs
      ClassicShellexScannerTests.cs
      EntryScannerTests.cs
      HideServiceTests.cs
  docs/
    superpowers/
      specs/2026-05-12-rcmm-design.md       # (already exists)
      plans/2026-05-12-rcmm-foundation-and-classic-hide.md  # this file
```

---

## Conventions

- TDD: every service has a failing test before implementation.
- `IRegistry` is the only seam between code and the live registry; tests always use `FakeRegistry`.
- Commit after each task. Commit message format: `<area>: <imperative summary>`.
- All registry path strings are lowercase-comparison; we always read/write with the canonical case from the spec.
- Test framework: xUnit 2.x. Assertion style: `Assert.Equal(expected, actual)`.

---

### Task 1: Solution + project skeleton

**Files:**
- Create: `manager/RCMM.sln`
- Create: `manager/src/RCMM/RCMM.csproj`
- Create: `manager/src/RCMM/app.manifest`
- Create: `manager/src/RCMM/App.xaml`
- Create: `manager/src/RCMM/App.xaml.cs`
- Create: `manager/src/RCMM/MainWindow.xaml`
- Create: `manager/src/RCMM/MainWindow.xaml.cs`
- Create: `manager/test/RCMM.Tests/RCMM.Tests.csproj`
- Create: `.gitignore` at repo root

- [ ] **Step 1: Create `.gitignore`**

Create `C:/Users/Admin/Documents/Claude/Github/RCMM/.gitignore`:

```
bin/
obj/
*.user
.vs/
*.suo
```

- [ ] **Step 2: Create the manager .csproj**

Create `manager/src/RCMM/RCMM.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <RootNamespace>RCMM</RootNamespace>
    <UseWinUI>true</UseWinUI>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <Nullable>enable</Nullable>
    <LangVersion>12</LangVersion>
    <Platforms>x64</Platforms>
    <RuntimeIdentifiers>win-x64</RuntimeIdentifiers>
    <WindowsPackageType>None</WindowsPackageType>
    <EnableMsixTooling>true</EnableMsixTooling>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.5.240607000" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.22621.756" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create `app.manifest` with requireAdministrator**

Create `manager/src/RCMM/app.manifest`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="RCMM"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false"/>
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/>
    </application>
  </compatibility>
</assembly>
```

- [ ] **Step 4: Create minimal App.xaml + App.xaml.cs**

`App.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Application
    x:Class="RCMM.App"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

`App.xaml.cs`:

```csharp
using Microsoft.UI.Xaml;

namespace RCMM;

public partial class App : Application
{
    private Window? _window;

    public App() { InitializeComponent(); }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
```

- [ ] **Step 5: Create minimal MainWindow.xaml + .xaml.cs**

`MainWindow.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Window
    x:Class="RCMM.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Title="RCMM">
    <Grid>
        <TextBlock Text="RCMM boots." VerticalAlignment="Center" HorizontalAlignment="Center"/>
    </Grid>
</Window>
```

`MainWindow.xaml.cs`:

```csharp
using Microsoft.UI.Xaml;

namespace RCMM;

public sealed partial class MainWindow : Window
{
    public MainWindow() { InitializeComponent(); }
}
```

- [ ] **Step 6: Create test project**

Create `manager/test/RCMM.Tests/RCMM.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>RCMM.Tests</RootNamespace>
    <Nullable>enable</Nullable>
    <LangVersion>12</LangVersion>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.10.0" />
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
</Project>
```

NOTE: The test project does NOT reference the WinUI project directly because WinUI projects don't multi-target plain net8.0 cleanly. We will extract a shared `RCMM.Core` library in Task 2 that both projects reference. The current step just lays out the test csproj scaffold.

- [ ] **Step 7: Create RCMM.sln**

Run from `manager/`:

```powershell
dotnet new sln -n RCMM
dotnet sln add src/RCMM/RCMM.csproj
dotnet sln add test/RCMM.Tests/RCMM.Tests.csproj
```

- [ ] **Step 8: Build and run**

```powershell
dotnet build manager/RCMM.sln
dotnet run --project manager/src/RCMM/RCMM.csproj
```

Expected: a UAC prompt appears (due to requireAdministrator), then a small window saying "RCMM boots." Close it.

- [ ] **Step 9: Commit**

```powershell
git add .gitignore manager
git commit -m "scaffold: empty WinUI 3 / .NET 8 RCMM solution"
```

---

### Task 2: Extract `RCMM.Core` shared library

The test project can't reference a WinUI csproj cleanly. Move all non-UI code into a `RCMM.Core` library that both `RCMM` (WinUI app) and `RCMM.Tests` reference.

**Files:**
- Create: `manager/src/RCMM.Core/RCMM.Core.csproj`
- Modify: `manager/src/RCMM/RCMM.csproj` (add ProjectReference)
- Modify: `manager/test/RCMM.Tests/RCMM.Tests.csproj` (add ProjectReference)
- Modify: `manager/RCMM.sln` (add project)

- [ ] **Step 1: Create Core csproj**

`manager/src/RCMM.Core/RCMM.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <RootNamespace>RCMM.Core</RootNamespace>
    <Nullable>enable</Nullable>
    <LangVersion>12</LangVersion>
    <UseWindowsForms>false</UseWindowsForms>
    <UseWPF>false</UseWPF>
  </PropertyGroup>
</Project>
```

`net8.0-windows` (not the WinUI-flavored TFM) is enough — we need `Microsoft.Win32.Registry` access but no UI types here.

- [ ] **Step 2: Wire references**

In `manager/src/RCMM/RCMM.csproj`, add inside `<ItemGroup>`:

```xml
<ProjectReference Include="..\RCMM.Core\RCMM.Core.csproj" />
```

In `manager/test/RCMM.Tests/RCMM.Tests.csproj`, change `<TargetFramework>` to `net8.0-windows` and add:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\RCMM.Core\RCMM.Core.csproj" />
</ItemGroup>
```

- [ ] **Step 3: Add to solution**

```powershell
dotnet sln manager/RCMM.sln add manager/src/RCMM.Core/RCMM.Core.csproj
```

- [ ] **Step 4: Build**

```powershell
dotnet build manager/RCMM.sln
```

Expected: build succeeds, three projects compile.

- [ ] **Step 5: Commit**

```powershell
git add manager
git commit -m "scaffold: split RCMM.Core library for testable non-UI code"
```

---

### Task 3: Models — Scope, EntryKind, ContextMenuEntry, PendingChange, Config

**Files:**
- Create: `manager/src/RCMM.Core/Models/Scope.cs`
- Create: `manager/src/RCMM.Core/Models/EntryKind.cs`
- Create: `manager/src/RCMM.Core/Models/ContextMenuEntry.cs`
- Create: `manager/src/RCMM.Core/Models/PendingChange.cs`
- Create: `manager/src/RCMM.Core/Models/Config.cs`

These are plain data shapes — no separate tests needed; they'll be exercised by service tests.

- [ ] **Step 1: Create `Scope.cs`**

```csharp
namespace RCMM.Core.Models;

public enum Scope
{
    Files,
    Folders,
    Drives,
    Background
}

public static class ScopeExtensions
{
    public static string ToRegistryRoot(this Scope scope) => scope switch
    {
        Scope.Files      => @"*",
        Scope.Folders    => @"Directory",
        Scope.Drives     => @"Drive",
        Scope.Background => @"Directory\Background",
        _ => throw new System.ArgumentOutOfRangeException(nameof(scope))
    };
}
```

- [ ] **Step 2: Create `EntryKind.cs`**

```csharp
namespace RCMM.Core.Models;

public enum EntryKind
{
    ShellVerb,
    ShellExtension
}
```

- [ ] **Step 3: Create `ContextMenuEntry.cs`**

```csharp
namespace RCMM.Core.Models;

public sealed record ContextMenuEntry
{
    public required string Id { get; init; }              // stable, derived from registry path
    public required string DisplayName { get; init; }
    public required string Source { get; init; }          // "Windows", "WinRAR", etc.
    public required Scope Scope { get; init; }
    public required EntryKind Kind { get; init; }
    public required string RegistryPath { get; init; }    // HKCR-relative
    public required string OriginalKeyName { get; init; }
    public string? IconPath { get; init; }
    public string? CommandLine { get; init; }             // ShellVerb only
    public string? Clsid { get; init; }                   // ShellExtension only
    public bool IsBuiltIn { get; init; }
    public bool IsHidden { get; init; }
}
```

- [ ] **Step 4: Create `PendingChange.cs`**

```csharp
namespace RCMM.Core.Models;

public enum PendingAction { Hide, Unhide }

public sealed record PendingChange(string EntryId, PendingAction Action, bool RequiresExplorerRestart);
```

- [ ] **Step 5: Create `Config.cs`**

```csharp
using System.Collections.Generic;

namespace RCMM.Core.Models;

public sealed class Config
{
    public int SchemaVersion { get; set; } = 1;
    public List<ContextMenuEntry> KnownEntries { get; set; } = new();
}
```

- [ ] **Step 6: Build**

```powershell
dotnet build manager/RCMM.sln
```

- [ ] **Step 7: Commit**

```powershell
git add manager
git commit -m "models: scope, entry, pending change, config"
```

---

### Task 4: `IRegistry` abstraction + `FakeRegistry`

**Files:**
- Create: `manager/src/RCMM.Core/Services/IRegistry.cs`
- Create: `manager/test/RCMM.Tests/FakeRegistry.cs`
- Create: `manager/test/RCMM.Tests/FakeRegistryTests.cs`

- [ ] **Step 1: Write failing FakeRegistry tests first**

`manager/test/RCMM.Tests/FakeRegistryTests.cs`:

```csharp
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class FakeRegistryTests
{
    [Fact]
    public void KeyExists_returns_false_for_unknown_path()
    {
        var reg = new FakeRegistry();
        Assert.False(reg.KeyExists(RegistryHive.ClassesRoot, @"Foo\Bar"));
    }

    [Fact]
    public void CreateKey_then_KeyExists_returns_true()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"Foo\Bar");
        Assert.True(reg.KeyExists(RegistryHive.ClassesRoot, @"Foo\Bar"));
    }

    [Fact]
    public void SetValue_then_GetValue_returns_value()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"Foo");
        reg.SetValue(RegistryHive.ClassesRoot, @"Foo", "Name", "Hello");
        Assert.Equal("Hello", reg.GetValue(RegistryHive.ClassesRoot, @"Foo", "Name"));
    }

    [Fact]
    public void GetValue_returns_null_for_unknown_value()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"Foo");
        Assert.Null(reg.GetValue(RegistryHive.ClassesRoot, @"Foo", "Missing"));
    }

    [Fact]
    public void GetSubKeyNames_lists_immediate_children_only()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"Root\A");
        reg.CreateKey(RegistryHive.ClassesRoot, @"Root\B");
        reg.CreateKey(RegistryHive.ClassesRoot, @"Root\B\Nested");

        var names = reg.GetSubKeyNames(RegistryHive.ClassesRoot, @"Root");
        Assert.Equal(new[] { "A", "B" }, names);
    }

    [Fact]
    public void DeleteValue_removes_value_but_keeps_key()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"Foo");
        reg.SetValue(RegistryHive.ClassesRoot, @"Foo", "X", "Y");
        reg.DeleteValue(RegistryHive.ClassesRoot, @"Foo", "X");
        Assert.True(reg.KeyExists(RegistryHive.ClassesRoot, @"Foo"));
        Assert.Null(reg.GetValue(RegistryHive.ClassesRoot, @"Foo", "X"));
    }

    [Fact]
    public void DeleteKey_recurses()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"Foo\Bar\Baz");
        reg.DeleteKey(RegistryHive.ClassesRoot, @"Foo");
        Assert.False(reg.KeyExists(RegistryHive.ClassesRoot, @"Foo"));
        Assert.False(reg.KeyExists(RegistryHive.ClassesRoot, @"Foo\Bar"));
    }
}
```

- [ ] **Step 2: Create `IRegistry.cs`**

```csharp
using System.Collections.Generic;

namespace RCMM.Core.Services;

public enum RegistryHive { ClassesRoot, CurrentUser, LocalMachine }

public interface IRegistry
{
    bool KeyExists(RegistryHive hive, string path);
    void CreateKey(RegistryHive hive, string path);
    void DeleteKey(RegistryHive hive, string path);
    void DeleteValue(RegistryHive hive, string path, string name);
    object? GetValue(RegistryHive hive, string path, string name);
    void SetValue(RegistryHive hive, string path, string name, object value);
    IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string path);
    IReadOnlyList<string> GetValueNames(RegistryHive hive, string path);
}
```

- [ ] **Step 3: Implement `FakeRegistry`**

`manager/test/RCMM.Tests/FakeRegistry.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using RCMM.Core.Services;

namespace RCMM.Tests;

public sealed class FakeRegistry : IRegistry
{
    private readonly Dictionary<(RegistryHive Hive, string Path), Dictionary<string, object>> _keys = new();

    public bool KeyExists(RegistryHive hive, string path) => _keys.ContainsKey((hive, Normalize(path)));

    public void CreateKey(RegistryHive hive, string path)
    {
        path = Normalize(path);
        // Materialize each ancestor so enumeration works.
        var parts = path.Split('\\');
        for (int i = 1; i <= parts.Length; i++)
        {
            var sub = string.Join('\\', parts[..i]);
            if (!_keys.ContainsKey((hive, sub)))
                _keys[(hive, sub)] = new Dictionary<string, object>();
        }
    }

    public void DeleteKey(RegistryHive hive, string path)
    {
        path = Normalize(path);
        var doomed = _keys.Keys
            .Where(k => k.Hive == hive && (k.Path == path || k.Path.StartsWith(path + "\\")))
            .ToList();
        foreach (var k in doomed) _keys.Remove(k);
    }

    public void DeleteValue(RegistryHive hive, string path, string name)
    {
        if (_keys.TryGetValue((hive, Normalize(path)), out var values))
            values.Remove(name);
    }

    public object? GetValue(RegistryHive hive, string path, string name)
        => _keys.TryGetValue((hive, Normalize(path)), out var values) && values.TryGetValue(name, out var v) ? v : null;

    public void SetValue(RegistryHive hive, string path, string name, object value)
    {
        CreateKey(hive, path);
        _keys[(hive, Normalize(path))][name] = value;
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string path)
    {
        path = Normalize(path);
        var prefix = path + "\\";
        return _keys.Keys
            .Where(k => k.Hive == hive && k.Path.StartsWith(prefix))
            .Select(k => k.Path[prefix.Length..])
            .Where(rest => !rest.Contains('\\'))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> GetValueNames(RegistryHive hive, string path)
        => _keys.TryGetValue((hive, Normalize(path)), out var values)
            ? values.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList()
            : Array.Empty<string>();

    private static string Normalize(string path) => path.Trim('\\');
}
```

- [ ] **Step 4: Run tests and verify pass**

```powershell
dotnet test manager/test/RCMM.Tests
```

Expected: 7 passed.

- [ ] **Step 5: Commit**

```powershell
git add manager
git commit -m "services: IRegistry abstraction + FakeRegistry"
```

---

### Task 5: `Win32Registry` concrete implementation

**Files:**
- Create: `manager/src/RCMM.Core/Services/Win32Registry.cs`

This is the live-registry implementation. No unit tests (would touch the real registry); we'll do manual sandbox verification in a later step.

- [ ] **Step 1: Add Microsoft.Win32.Registry package**

In `manager/src/RCMM.Core/RCMM.Core.csproj`, inside `<ItemGroup>`:

```xml
<PackageReference Include="Microsoft.Win32.Registry" Version="5.0.0" />
```

- [ ] **Step 2: Create `Win32Registry.cs`**

```csharp
using System;
using System.Collections.Generic;
using Win32 = Microsoft.Win32;

namespace RCMM.Core.Services;

public sealed class Win32Registry : IRegistry
{
    public bool KeyExists(RegistryHive hive, string path)
    {
        using var k = OpenSubKey(hive, path, writable: false);
        return k != null;
    }

    public void CreateKey(RegistryHive hive, string path)
    {
        using var k = Root(hive).CreateSubKey(path, writable: true);
    }

    public void DeleteKey(RegistryHive hive, string path)
    {
        Root(hive).DeleteSubKeyTree(path, throwOnMissingSubKey: false);
    }

    public void DeleteValue(RegistryHive hive, string path, string name)
    {
        using var k = Root(hive).OpenSubKey(path, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    public object? GetValue(RegistryHive hive, string path, string name)
    {
        using var k = OpenSubKey(hive, path, writable: false);
        return k?.GetValue(name);
    }

    public void SetValue(RegistryHive hive, string path, string name, object value)
    {
        using var k = Root(hive).CreateSubKey(path, writable: true);
        k.SetValue(name, value);
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string path)
    {
        using var k = OpenSubKey(hive, path, writable: false);
        return k?.GetSubKeyNames() ?? Array.Empty<string>();
    }

    public IReadOnlyList<string> GetValueNames(RegistryHive hive, string path)
    {
        using var k = OpenSubKey(hive, path, writable: false);
        return k?.GetValueNames() ?? Array.Empty<string>();
    }

    private static Win32.RegistryKey Root(RegistryHive hive) => hive switch
    {
        RegistryHive.ClassesRoot  => Win32.Registry.ClassesRoot,
        RegistryHive.CurrentUser  => Win32.Registry.CurrentUser,
        RegistryHive.LocalMachine => Win32.Registry.LocalMachine,
        _ => throw new ArgumentOutOfRangeException(nameof(hive))
    };

    private static Win32.RegistryKey? OpenSubKey(RegistryHive hive, string path, bool writable)
        => Root(hive).OpenSubKey(path, writable);
}
```

- [ ] **Step 3: Build**

```powershell
dotnet build manager/RCMM.sln
```

- [ ] **Step 4: Commit**

```powershell
git add manager
git commit -m "services: Win32Registry implementation"
```

---

### Task 6: `ConfigStore` — load/save config JSON with debounce

**Files:**
- Create: `manager/src/RCMM.Core/Services/ConfigStore.cs`
- Create: `manager/test/RCMM.Tests/ConfigStoreTests.cs`

- [ ] **Step 1: Write failing tests**

`manager/test/RCMM.Tests/ConfigStoreTests.cs`:

```csharp
using System.IO;
using System.Threading.Tasks;
using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class ConfigStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"rcmm-{System.Guid.NewGuid():N}.json");

    [Fact]
    public async Task LoadAsync_returns_default_when_file_missing()
    {
        var store = new ConfigStore(TempPath());
        var cfg = await store.LoadAsync();
        Assert.Equal(1, cfg.SchemaVersion);
        Assert.Empty(cfg.KnownEntries);
    }

    [Fact]
    public async Task SaveImmediateAsync_then_LoadAsync_roundtrip()
    {
        var path = TempPath();
        try
        {
            var store = new ConfigStore(path);
            var cfg = new Config();
            cfg.KnownEntries.Add(new ContextMenuEntry
            {
                Id = "files/shell/foo", DisplayName = "Foo", Source = "Test",
                Scope = Scope.Files, Kind = EntryKind.ShellVerb,
                RegistryPath = @"*\shell\foo", OriginalKeyName = "foo"
            });
            await store.SaveImmediateAsync(cfg);

            var reloaded = await store.LoadAsync();
            Assert.Single(reloaded.KnownEntries);
            Assert.Equal("Foo", reloaded.KnownEntries[0].DisplayName);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadAsync_returns_default_on_malformed_json()
    {
        var path = TempPath();
        try
        {
            await File.WriteAllTextAsync(path, "not-json");
            var store = new ConfigStore(path);
            var cfg = await store.LoadAsync();
            Assert.Equal(1, cfg.SchemaVersion);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: Run tests — verify they fail (compile error: ConfigStore doesn't exist)**

```powershell
dotnet test manager/test/RCMM.Tests
```

Expected: build error referencing missing `ConfigStore`.

- [ ] **Step 3: Implement `ConfigStore.cs`**

`manager/src/RCMM.Core/Services/ConfigStore.cs`:

```csharp
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _debounceCts;

    public string ConfigPath => _path;

    public ConfigStore() : this(DefaultPath()) { }
    public ConfigStore(string path) { _path = path; }

    public static string DefaultPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "RCMM", "config.json");

    public async Task<Config> LoadAsync()
    {
        if (!File.Exists(_path)) return new Config();
        try
        {
            await using var fs = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<Config>(fs, JsonOpts) ?? new Config();
        }
        catch (JsonException) { return new Config(); }
    }

    public void ScheduleSave(Config cfg)
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(200, token); await SaveImmediateAsync(cfg); }
            catch (TaskCanceledException) { }
        });
    }

    public async Task SaveImmediateAsync(Config cfg)
    {
        await _writeLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            await using (var fs = File.Create(tmp))
                await JsonSerializer.SerializeAsync(fs, cfg, JsonOpts);
            File.Move(tmp, _path, overwrite: true);
        }
        finally { _writeLock.Release(); }
    }
}
```

- [ ] **Step 4: Run tests — verify pass**

```powershell
dotnet test manager/test/RCMM.Tests
```

Expected: 10 passed (7 + 3).

- [ ] **Step 5: Commit**

```powershell
git add manager
git commit -m "services: ConfigStore with debounce and atomic write"
```

---

### Task 7: `ClsidResolver` — CLSID → DLL → friendly name + icon path

A CLSID like `{B41DB860-8EE4-11D2-9906-E49FADC173CA}` (WinRAR) resolves to a DLL via `HKCR\CLSID\<CLSID>\InprocServer32` and we read the DLL's `FileDescription`. The DLL path is also the icon source.

**Files:**
- Create: `manager/src/RCMM.Core/Services/ClsidResolver.cs`
- Create: `manager/test/RCMM.Tests/ClsidResolverTests.cs`

- [ ] **Step 1: Write failing tests**

`manager/test/RCMM.Tests/ClsidResolverTests.cs`:

```csharp
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class ClsidResolverTests
{
    [Fact]
    public void Resolve_returns_null_for_unknown_clsid()
    {
        var reg = new FakeRegistry();
        var sut = new ClsidResolver(reg);
        Assert.Null(sut.Resolve("{DEAD-BEEF}"));
    }

    [Fact]
    public void Resolve_returns_dll_path_and_default_name()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{ABC}\InprocServer32", "", @"C:\Path\my.dll");
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{ABC}", "", "FriendlyName");
        var sut = new ClsidResolver(reg);

        var info = sut.Resolve("{ABC}");
        Assert.NotNull(info);
        Assert.Equal(@"C:\Path\my.dll", info!.DllPath);
        Assert.Equal("FriendlyName", info.DefaultName);
    }

    [Fact]
    public void Resolve_handles_missing_default_name()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{ABC}\InprocServer32", "", @"C:\Path\my.dll");
        reg.CreateKey(RegistryHive.ClassesRoot, @"CLSID\{ABC}");
        var sut = new ClsidResolver(reg);

        var info = sut.Resolve("{ABC}");
        Assert.NotNull(info);
        Assert.Null(info!.DefaultName);
    }
}
```

- [ ] **Step 2: Implement `ClsidResolver.cs`**

```csharp
namespace RCMM.Core.Services;

public sealed record ClsidInfo(string Clsid, string? DllPath, string? DefaultName);

public sealed class ClsidResolver
{
    private readonly IRegistry _reg;

    public ClsidResolver(IRegistry reg) { _reg = reg; }

    public ClsidInfo? Resolve(string clsid)
    {
        var keyPath = $@"CLSID\{clsid}";
        if (!_reg.KeyExists(RegistryHive.ClassesRoot, keyPath)) return null;

        var dll = _reg.GetValue(RegistryHive.ClassesRoot, $@"CLSID\{clsid}\InprocServer32", "") as string;
        var defaultName = _reg.GetValue(RegistryHive.ClassesRoot, keyPath, "") as string;
        return new ClsidInfo(clsid, dll, defaultName);
    }
}
```

NOTE: Reading the DLL's `FileDescription` requires `System.Diagnostics.FileVersionInfo`, which touches the file system. We'll add that *outside* the resolver in the UI/services layer when we want a friendly source name from a DLL; for testability, resolve here returns just the DLL path + key default.

- [ ] **Step 3: Run tests — verify pass**

```powershell
dotnet test manager/test/RCMM.Tests
```

Expected: 13 passed.

- [ ] **Step 4: Commit**

```powershell
git add manager
git commit -m "services: ClsidResolver maps CLSID to InprocServer32 dll"
```

---

### Task 8: `ClassicVerbScanner` — enumerate `HKCR\<scope>\shell\*`

**Files:**
- Create: `manager/src/RCMM.Core/Services/ClassicVerbScanner.cs`
- Create: `manager/test/RCMM.Tests/ClassicVerbScannerTests.cs`

- [ ] **Step 1: Write failing tests**

`manager/test/RCMM.Tests/ClassicVerbScannerTests.cs`:

```csharp
using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class ClassicVerbScannerTests
{
    [Fact]
    public void Scan_returns_empty_when_no_shell_key()
    {
        var reg = new FakeRegistry();
        var sut = new ClassicVerbScanner(reg);
        Assert.Empty(sut.Scan(Scope.Files));
    }

    [Fact]
    public void Scan_finds_a_simple_verb()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\openwith", "", "Open with…");
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\openwith\command", "", @"openwithhelper.exe ""%1""");
        var sut = new ClassicVerbScanner(reg);

        var entries = sut.Scan(Scope.Files).ToList();
        Assert.Single(entries);
        Assert.Equal("Open with…", entries[0].DisplayName);
        Assert.Equal(EntryKind.ShellVerb, entries[0].Kind);
        Assert.Equal(Scope.Files, entries[0].Scope);
        Assert.False(entries[0].IsHidden);
        Assert.Equal(@"openwithhelper.exe ""%1""", entries[0].CommandLine);
    }

    [Fact]
    public void Scan_falls_back_to_key_name_when_no_display_name()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"*\shell\runme");
        var sut = new ClassicVerbScanner(reg);

        var entries = sut.Scan(Scope.Files).ToList();
        Assert.Equal("runme", entries[0].DisplayName);
    }

    [Fact]
    public void Scan_detects_LegacyDisable_as_hidden()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\thing", "", "Thing");
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\thing", "LegacyDisable", "");
        var sut = new ClassicVerbScanner(reg);

        Assert.True(sut.Scan(Scope.Files).Single().IsHidden);
    }

    [Fact]
    public void Scan_respects_scope_root()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\fileverb", "", "FileVerb");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\folderverb", "", "FolderVerb");
        var sut = new ClassicVerbScanner(reg);

        Assert.Equal("FileVerb", sut.Scan(Scope.Files).Single().DisplayName);
        Assert.Equal("FolderVerb", sut.Scan(Scope.Folders).Single().DisplayName);
    }
}
```

- [ ] **Step 2: Implement `ClassicVerbScanner.cs`**

```csharp
using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public sealed class ClassicVerbScanner
{
    private readonly IRegistry _reg;

    public ClassicVerbScanner(IRegistry reg) { _reg = reg; }

    public IEnumerable<ContextMenuEntry> Scan(Scope scope)
    {
        var root = scope.ToRegistryRoot() + @"\shell";
        if (!_reg.KeyExists(RegistryHive.ClassesRoot, root)) yield break;

        foreach (var name in _reg.GetSubKeyNames(RegistryHive.ClassesRoot, root))
        {
            var path = root + "\\" + name;
            var display = _reg.GetValue(RegistryHive.ClassesRoot, path, "") as string;
            var hidden = _reg.GetValue(RegistryHive.ClassesRoot, path, "LegacyDisable") != null;
            var commandLine = _reg.GetValue(RegistryHive.ClassesRoot, path + @"\command", "") as string;

            yield return new ContextMenuEntry
            {
                Id = $"{scope}/shell/{name}",
                DisplayName = string.IsNullOrEmpty(display) ? name : display!,
                Source = "Unknown",
                Scope = scope,
                Kind = EntryKind.ShellVerb,
                RegistryPath = path,
                OriginalKeyName = name,
                IsBuiltIn = false,
                IsHidden = hidden,
                CommandLine = commandLine
            };
        }
    }
}
```

- [ ] **Step 3: Run tests — verify pass**

```powershell
dotnet test manager/test/RCMM.Tests
```

Expected: 18 passed.

- [ ] **Step 4: Commit**

```powershell
git add manager
git commit -m "services: ClassicVerbScanner enumerates shell verbs per scope"
```

---

### Task 9: `ClassicShellexScanner` — enumerate `HKCR\<scope>\shellex\ContextMenuHandlers\*`

These entries are CLSIDs (the handler is a COM object). The default value of each subkey is either the CLSID itself or a friendly name; if not a CLSID, the *key name* often is. Hidden state = an HKCU mask key exists with empty default.

**Files:**
- Create: `manager/src/RCMM.Core/Services/ClassicShellexScanner.cs`
- Create: `manager/test/RCMM.Tests/ClassicShellexScannerTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class ClassicShellexScannerTests
{
    [Fact]
    public void Scan_returns_empty_when_no_handlers_key()
    {
        var reg = new FakeRegistry();
        var sut = new ClassicShellexScanner(reg, new ClsidResolver(reg));
        Assert.Empty(sut.Scan(Scope.Files));
    }

    [Fact]
    public void Scan_finds_handler_by_clsid_default_value()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\WinRARShell", "", "{ABC}");
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{ABC}", "", "WinRAR Shell");
        var sut = new ClassicShellexScanner(reg, new ClsidResolver(reg));

        var entries = sut.Scan(Scope.Files).ToList();
        Assert.Single(entries);
        Assert.Equal("WinRARShell", entries[0].OriginalKeyName);
        Assert.Equal("{ABC}", entries[0].Clsid);
        Assert.Equal(EntryKind.ShellExtension, entries[0].Kind);
    }

    [Fact]
    public void Scan_uses_key_name_as_clsid_if_no_default_value()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\{XYZ}");
        var sut = new ClassicShellexScanner(reg, new ClsidResolver(reg));

        Assert.Equal("{XYZ}", sut.Scan(Scope.Files).Single().Clsid);
    }

    [Fact]
    public void Scan_detects_hidden_via_HKCU_mask()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\Foo", "", "{ABC}");
        // mask key in HKCU with empty default
        reg.SetValue(RegistryHive.CurrentUser, @"Software\Classes\*\shellex\ContextMenuHandlers\Foo", "", "");
        var sut = new ClassicShellexScanner(reg, new ClsidResolver(reg));

        Assert.True(sut.Scan(Scope.Files).Single().IsHidden);
    }
}
```

- [ ] **Step 2: Implement `ClassicShellexScanner.cs`**

```csharp
using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public sealed class ClassicShellexScanner
{
    private readonly IRegistry _reg;
    private readonly ClsidResolver _clsids;

    public ClassicShellexScanner(IRegistry reg, ClsidResolver clsids)
    {
        _reg = reg;
        _clsids = clsids;
    }

    public IEnumerable<ContextMenuEntry> Scan(Scope scope)
    {
        var root = scope.ToRegistryRoot() + @"\shellex\ContextMenuHandlers";
        if (!_reg.KeyExists(RegistryHive.ClassesRoot, root)) yield break;

        foreach (var name in _reg.GetSubKeyNames(RegistryHive.ClassesRoot, root))
        {
            var path = root + "\\" + name;
            var defaultVal = _reg.GetValue(RegistryHive.ClassesRoot, path, "") as string;
            var clsid = LooksLikeClsid(defaultVal) ? defaultVal! :
                        LooksLikeClsid(name) ? name : defaultVal ?? name;

            var resolved = _clsids.Resolve(clsid);
            var display = resolved?.DefaultName ?? name;

            var maskPath = @"Software\Classes\" + scope.ToRegistryRoot() + @"\shellex\ContextMenuHandlers\" + name;
            var hidden = _reg.KeyExists(RegistryHive.CurrentUser, maskPath);

            yield return new ContextMenuEntry
            {
                Id = $"{scope}/shellex/{name}",
                DisplayName = display,
                Source = "Unknown",
                Scope = scope,
                Kind = EntryKind.ShellExtension,
                RegistryPath = path,
                OriginalKeyName = name,
                IsBuiltIn = false,
                IsHidden = hidden,
                Clsid = clsid
            };
        }
    }

    private static bool LooksLikeClsid(string? s)
        => !string.IsNullOrEmpty(s) && s.StartsWith('{') && s.EndsWith('}');
}
```

- [ ] **Step 3: Run tests — verify pass**

```powershell
dotnet test manager/test/RCMM.Tests
```

Expected: 22 passed.

- [ ] **Step 4: Commit**

```powershell
git add manager
git commit -m "services: ClassicShellexScanner with HKCU mask detection"
```

---

### Task 10: `EntryScanner` — orchestrator over all scopes + kinds

**Files:**
- Create: `manager/src/RCMM.Core/Services/EntryScanner.cs`
- Create: `manager/test/RCMM.Tests/EntryScannerTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class EntryScannerTests
{
    [Fact]
    public void ScanAll_combines_verbs_and_shellex_across_scopes()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "", "Foo");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shellex\ContextMenuHandlers\Bar", "", "{X}");

        var sut = new EntryScanner(
            new ClassicVerbScanner(reg),
            new ClassicShellexScanner(reg, new ClsidResolver(reg)));

        var all = sut.ScanAll().ToList();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, e => e.Scope == Scope.Files && e.Kind == EntryKind.ShellVerb);
        Assert.Contains(all, e => e.Scope == Scope.Folders && e.Kind == EntryKind.ShellExtension);
    }

    [Fact]
    public void ScanScope_filters_to_that_scope_only()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\a", "", "A");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\b", "", "B");

        var sut = new EntryScanner(
            new ClassicVerbScanner(reg),
            new ClassicShellexScanner(reg, new ClsidResolver(reg)));

        var files = sut.ScanScope(Scope.Files).ToList();
        Assert.Single(files);
        Assert.Equal("A", files[0].DisplayName);
    }
}
```

- [ ] **Step 2: Implement `EntryScanner.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public sealed class EntryScanner
{
    private static readonly Scope[] AllScopes =
        { Scope.Files, Scope.Folders, Scope.Drives, Scope.Background };

    private readonly ClassicVerbScanner _verbs;
    private readonly ClassicShellexScanner _shellex;

    public EntryScanner(ClassicVerbScanner verbs, ClassicShellexScanner shellex)
    {
        _verbs = verbs;
        _shellex = shellex;
    }

    public IEnumerable<ContextMenuEntry> ScanAll()
        => AllScopes.SelectMany(ScanScope);

    public IEnumerable<ContextMenuEntry> ScanScope(Scope scope)
        => _verbs.Scan(scope).Concat(_shellex.Scan(scope));
}
```

- [ ] **Step 3: Run tests — verify pass**

```powershell
dotnet test manager/test/RCMM.Tests
```

Expected: 24 passed.

- [ ] **Step 4: Commit**

```powershell
git add manager
git commit -m "services: EntryScanner orchestrator"
```

---

### Task 11: `HideService` — hide/unhide entries via registry

**Files:**
- Create: `manager/src/RCMM.Core/Services/HideService.cs`
- Create: `manager/test/RCMM.Tests/HideServiceTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class HideServiceTests
{
    [Fact]
    public void Hide_classic_verb_sets_LegacyDisable()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "", "Foo");
        var sut = new HideService(reg);

        var entry = new ContextMenuEntry
        {
            Id = "Files/shell/foo", DisplayName = "Foo", Source = "Test",
            Scope = Scope.Files, Kind = EntryKind.ShellVerb,
            RegistryPath = @"*\shell\foo", OriginalKeyName = "foo"
        };
        sut.Hide(entry);

        Assert.Equal("", reg.GetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "LegacyDisable"));
    }

    [Fact]
    public void Unhide_classic_verb_removes_LegacyDisable()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "LegacyDisable", "");
        var sut = new HideService(reg);

        var entry = new ContextMenuEntry
        {
            Id = "Files/shell/foo", DisplayName = "Foo", Source = "Test",
            Scope = Scope.Files, Kind = EntryKind.ShellVerb,
            RegistryPath = @"*\shell\foo", OriginalKeyName = "foo"
        };
        sut.Unhide(entry);

        Assert.Null(reg.GetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "LegacyDisable"));
    }

    [Fact]
    public void Hide_classic_shellex_creates_HKCU_mask_key()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\WinRAR", "", "{X}");
        var sut = new HideService(reg);

        var entry = new ContextMenuEntry
        {
            Id = "Files/shellex/WinRAR", DisplayName = "WinRAR", Source = "Test",
            Scope = Scope.Files, Kind = EntryKind.ShellExtension,
            RegistryPath = @"*\shellex\ContextMenuHandlers\WinRAR", OriginalKeyName = "WinRAR",
            Clsid = "{X}"
        };
        sut.Hide(entry);

        Assert.True(reg.KeyExists(
            RegistryHive.CurrentUser,
            @"Software\Classes\*\shellex\ContextMenuHandlers\WinRAR"));
    }

    [Fact]
    public void Unhide_classic_shellex_removes_HKCU_mask_key()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.CurrentUser, @"Software\Classes\*\shellex\ContextMenuHandlers\WinRAR");
        var sut = new HideService(reg);

        var entry = new ContextMenuEntry
        {
            Id = "Files/shellex/WinRAR", DisplayName = "WinRAR", Source = "Test",
            Scope = Scope.Files, Kind = EntryKind.ShellExtension,
            RegistryPath = @"*\shellex\ContextMenuHandlers\WinRAR", OriginalKeyName = "WinRAR",
            Clsid = "{X}"
        };
        sut.Unhide(entry);

        Assert.False(reg.KeyExists(
            RegistryHive.CurrentUser,
            @"Software\Classes\*\shellex\ContextMenuHandlers\WinRAR"));
    }

    [Theory]
    [InlineData(EntryKind.ShellVerb, false)]
    [InlineData(EntryKind.ShellExtension, true)]
    public void RequiresExplorerRestart_only_for_shell_extensions(EntryKind kind, bool expected)
    {
        Assert.Equal(expected, HideService.RequiresExplorerRestart(kind));
    }
}
```

- [ ] **Step 2: Implement `HideService.cs`**

```csharp
using RCMM.Core.Models;

namespace RCMM.Core.Services;

public sealed class HideService
{
    private readonly IRegistry _reg;

    public HideService(IRegistry reg) { _reg = reg; }

    public void Hide(ContextMenuEntry entry)
    {
        switch (entry.Kind)
        {
            case EntryKind.ShellVerb:
                _reg.SetValue(RegistryHive.ClassesRoot, entry.RegistryPath, "LegacyDisable", "");
                break;
            case EntryKind.ShellExtension:
                _reg.CreateKey(RegistryHive.CurrentUser, MaskPath(entry));
                _reg.SetValue(RegistryHive.CurrentUser, MaskPath(entry), "", "");
                break;
        }
    }

    public void Unhide(ContextMenuEntry entry)
    {
        switch (entry.Kind)
        {
            case EntryKind.ShellVerb:
                _reg.DeleteValue(RegistryHive.ClassesRoot, entry.RegistryPath, "LegacyDisable");
                break;
            case EntryKind.ShellExtension:
                _reg.DeleteKey(RegistryHive.CurrentUser, MaskPath(entry));
                break;
        }
    }

    public static bool RequiresExplorerRestart(EntryKind kind) => kind == EntryKind.ShellExtension;

    private static string MaskPath(ContextMenuEntry entry)
        => @"Software\Classes\" + entry.Scope.ToRegistryRoot()
           + @"\shellex\ContextMenuHandlers\" + entry.OriginalKeyName;
}
```

- [ ] **Step 3: Run tests — verify pass**

```powershell
dotnet test manager/test/RCMM.Tests
```

Expected: 29 passed.

- [ ] **Step 4: Commit**

```powershell
git add manager
git commit -m "services: HideService for verb LegacyDisable and shellex masking"
```

---

### Task 12: `ExplorerRestart` — kill and respawn Explorer

**Files:**
- Create: `manager/src/RCMM.Core/Services/ExplorerRestart.cs`

No unit tests — this calls live processes. We'll smoke-test manually.

- [ ] **Step 1: Create `ExplorerRestart.cs`**

```csharp
using System.Diagnostics;

namespace RCMM.Core.Services;

public sealed class ExplorerRestart
{
    public void Restart()
    {
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try { p.Kill(); } catch { /* ignore */ }
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        });
    }
}
```

- [ ] **Step 2: Build**

```powershell
dotnet build manager/RCMM.sln
```

- [ ] **Step 3: Commit**

```powershell
git add manager
git commit -m "services: ExplorerRestart"
```

---

### Task 13: ViewModels — `ObservableObject`, `EntryRowViewModel`, `ScopeListViewModel`, `MainViewModel`

**Files:**
- Create: `manager/src/RCMM.Core/ViewModels/ObservableObject.cs`
- Create: `manager/src/RCMM.Core/ViewModels/EntryRowViewModel.cs`
- Create: `manager/src/RCMM.Core/ViewModels/ScopeListViewModel.cs`
- Create: `manager/src/RCMM.Core/ViewModels/MainViewModel.cs`
- Create: `manager/test/RCMM.Tests/MainViewModelTests.cs`

- [ ] **Step 1: Write failing MainViewModel tests**

`manager/test/RCMM.Tests/MainViewModelTests.cs`:

```csharp
using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;
using Xunit;

namespace RCMM.Tests;

public class MainViewModelTests
{
    private static (MainViewModel vm, FakeRegistry reg) BuildSut()
    {
        var reg = new FakeRegistry();
        var scanner = new EntryScanner(
            new ClassicVerbScanner(reg),
            new ClassicShellexScanner(reg, new ClsidResolver(reg)));
        var hide = new HideService(reg);
        return (new MainViewModel(scanner, hide), reg);
    }

    [Fact]
    public void Rescan_populates_per_scope_lists()
    {
        var (vm, reg) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\a", "", "A");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\b", "", "B");

        vm.Rescan();

        Assert.Single(vm.GetScope(Scope.Files).Entries);
        Assert.Single(vm.GetScope(Scope.Folders).Entries);
        Assert.Empty(vm.GetScope(Scope.Drives).Entries);
    }

    [Fact]
    public void ToggleEntry_records_pending_change_and_does_not_yet_touch_registry()
    {
        var (vm, reg) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\a", "", "A");
        vm.Rescan();

        var row = vm.GetScope(Scope.Files).Entries.First();
        row.IsHidden = true;

        Assert.Single(vm.PendingChanges);
        Assert.Equal(PendingAction.Hide, vm.PendingChanges.First().Action);
        // Verb-only changes do NOT require an explorer restart
        Assert.False(vm.RequiresExplorerRestart);
    }

    [Fact]
    public void ApplyPending_writes_to_registry_and_clears_pending()
    {
        var (vm, reg) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\a", "", "A");
        vm.Rescan();

        vm.GetScope(Scope.Files).Entries.First().IsHidden = true;
        vm.ApplyPending();

        Assert.Equal("", reg.GetValue(RegistryHive.ClassesRoot, @"*\shell\a", "LegacyDisable"));
        Assert.Empty(vm.PendingChanges);
    }

    [Fact]
    public void Shellex_toggle_sets_RequiresExplorerRestart()
    {
        var (vm, reg) = BuildSut();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\X", "", "{Y}");
        vm.Rescan();

        vm.GetScope(Scope.Files).Entries.First().IsHidden = true;

        Assert.True(vm.RequiresExplorerRestart);
    }
}
```

- [ ] **Step 2: Implement `ObservableObject.cs`**

```csharp
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RCMM.Core.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

- [ ] **Step 3: Implement `EntryRowViewModel.cs`**

```csharp
using System;
using RCMM.Core.Models;

namespace RCMM.Core.ViewModels;

public sealed class EntryRowViewModel : ObservableObject
{
    private bool _isHidden;
    public ContextMenuEntry Entry { get; }
    public Action<EntryRowViewModel, bool>? HiddenChanged;

    public EntryRowViewModel(ContextMenuEntry entry)
    {
        Entry = entry;
        _isHidden = entry.IsHidden;
    }

    public string DisplayName => Entry.DisplayName;
    public string Source => Entry.Source;
    public string KindLabel => Entry.Kind switch
    {
        EntryKind.ShellVerb     => "Verb",
        EntryKind.ShellExtension => "Shell extension",
        _ => "?"
    };
    public bool IsBuiltIn => Entry.IsBuiltIn;

    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (SetField(ref _isHidden, value))
                HiddenChanged?.Invoke(this, value);
        }
    }
}
```

- [ ] **Step 4: Implement `ScopeListViewModel.cs`**

```csharp
using System.Collections.ObjectModel;
using RCMM.Core.Models;

namespace RCMM.Core.ViewModels;

public sealed class ScopeListViewModel : ObservableObject
{
    public Scope Scope { get; }
    public ObservableCollection<EntryRowViewModel> Entries { get; } = new();

    public ScopeListViewModel(Scope scope) { Scope = scope; }
}
```

- [ ] **Step 5: Implement `MainViewModel.cs`**

```csharp
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Core.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly Scope[] AllScopes =
        { Scope.Files, Scope.Folders, Scope.Drives, Scope.Background };

    private readonly EntryScanner _scanner;
    private readonly HideService _hideService;
    private readonly Dictionary<Scope, ScopeListViewModel> _scopes;
    private readonly Dictionary<string, PendingChange> _pending = new();

    public ObservableCollection<PendingChange> PendingChanges { get; } = new();

    public MainViewModel(EntryScanner scanner, HideService hideService)
    {
        _scanner = scanner;
        _hideService = hideService;
        _scopes = AllScopes.ToDictionary(s => s, s => new ScopeListViewModel(s));
    }

    public ScopeListViewModel GetScope(Scope scope) => _scopes[scope];

    public bool RequiresExplorerRestart
        => _pending.Values.Any(p => p.RequiresExplorerRestart);

    public void Rescan()
    {
        foreach (var scope in AllScopes)
            _scopes[scope].Entries.Clear();

        foreach (var entry in _scanner.ScanAll())
        {
            var row = new EntryRowViewModel(entry);
            row.HiddenChanged = OnRowToggled;
            _scopes[entry.Scope].Entries.Add(row);
        }

        _pending.Clear();
        PendingChanges.Clear();
        Raise(nameof(RequiresExplorerRestart));
    }

    private void OnRowToggled(EntryRowViewModel row, bool isHidden)
    {
        var action = isHidden ? PendingAction.Hide : PendingAction.Unhide;
        // If the row's new state matches the underlying entry, drop the pending change.
        if (isHidden == row.Entry.IsHidden)
        {
            if (_pending.Remove(row.Entry.Id, out var stale))
                PendingChanges.Remove(stale);
        }
        else
        {
            var change = new PendingChange(row.Entry.Id, action,
                HideService.RequiresExplorerRestart(row.Entry.Kind));
            if (_pending.TryGetValue(row.Entry.Id, out var existing))
                PendingChanges.Remove(existing);
            _pending[row.Entry.Id] = change;
            PendingChanges.Add(change);
        }
        Raise(nameof(RequiresExplorerRestart));
    }

    public void ApplyPending()
    {
        foreach (var change in _pending.Values.ToList())
        {
            var entry = AllScopes
                .SelectMany(s => _scopes[s].Entries)
                .First(r => r.Entry.Id == change.EntryId)
                .Entry;

            if (change.Action == PendingAction.Hide) _hideService.Hide(entry);
            else _hideService.Unhide(entry);
        }
        _pending.Clear();
        PendingChanges.Clear();
        Raise(nameof(RequiresExplorerRestart));
    }
}
```

- [ ] **Step 6: Run tests — verify pass**

```powershell
dotnet test manager/test/RCMM.Tests
```

Expected: 33 passed.

- [ ] **Step 7: Commit**

```powershell
git add manager
git commit -m "viewmodels: main / scope-list / entry-row with pending-change tracking"
```

---

### Task 14: WinUI shell — title bar, theming, MainWindow surface

We bring in the same title-bar/theming patterns Hide-Any-Window uses so RCMM has the same look. Steal the structure from `Hide-Any-Window/manager/src/HideAnyWindowManager/MainWindow.xaml.cs` and adapt.

**Files:**
- Modify: `manager/src/RCMM/App.xaml` (add WindowBackground / FooterBackground brushes)
- Modify: `manager/src/RCMM/MainWindow.xaml` (title bar + content host + footer)
- Modify: `manager/src/RCMM/MainWindow.xaml.cs` (theming, title-bar setup, footer wiring)
- Create: `manager/src/RCMM/Util/Win32.cs` (DPI / DWM border helpers)
- Create: `manager/src/RCMM/Util/WindowMinSize.cs`

- [ ] **Step 1: Replace `App.xaml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<Application
    x:Class="RCMM.App"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
            </ResourceDictionary.MergedDictionaries>
            <ResourceDictionary.ThemeDictionaries>
                <ResourceDictionary x:Key="Default">
                    <SolidColorBrush x:Key="WindowBackground" Color="#FFF3F3F3"/>
                    <SolidColorBrush x:Key="FooterBackground" Color="#FFE9E9E9"/>
                </ResourceDictionary>
                <ResourceDictionary x:Key="Light">
                    <SolidColorBrush x:Key="WindowBackground" Color="#FFF3F3F3"/>
                    <SolidColorBrush x:Key="FooterBackground" Color="#FFE9E9E9"/>
                </ResourceDictionary>
                <ResourceDictionary x:Key="Dark">
                    <SolidColorBrush x:Key="WindowBackground" Color="#FF202020"/>
                    <SolidColorBrush x:Key="FooterBackground" Color="#FF2B2B2B"/>
                </ResourceDictionary>
            </ResourceDictionary.ThemeDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 2: Create `Util/Win32.cs`**

```csharp
using System.Runtime.InteropServices;

namespace RCMM.Util;

internal static class Win32
{
    [DllImport("User32.dll")] internal static extern uint GetDpiForWindow(System.IntPtr hwnd);
    [DllImport("Dwmapi.dll")] internal static extern int DwmSetWindowAttribute(System.IntPtr hwnd, uint attr, ref uint value, int size);
    internal const uint DWMWA_BORDER_COLOR = 34;
    internal const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
}
```

- [ ] **Step 3: Create `Util/WindowMinSize.cs`**

```csharp
using System;
using System.Runtime.InteropServices;

namespace RCMM.Util;

internal sealed class WindowMinSize
{
    private const int GWLP_WNDPROC = -4;
    private const int WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrev, IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);

    private readonly IntPtr _hwnd;
    private readonly int _minDipW, _minDipH;
    private readonly IntPtr _prevProc;
    private readonly WndProc _newProc;

    private WindowMinSize(IntPtr hwnd, int minDipW, int minDipH)
    {
        _hwnd = hwnd;
        _minDipW = minDipW;
        _minDipH = minDipH;
        _newProc = Hook;
        _prevProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_newProc));
    }

    public static WindowMinSize Apply(IntPtr hwnd, int minDipW, int minDipH)
        => new(hwnd, minDipW, minDipH);

    private IntPtr Hook(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp)
    {
        if (msg == WM_GETMINMAXINFO && hwnd == _hwnd)
        {
            var dpi = Win32.GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lp);
            mmi.ptMinTrackSize.X = (int)(_minDipW * dpi / 96.0);
            mmi.ptMinTrackSize.Y = (int)(_minDipH * dpi / 96.0);
            Marshal.StructureToPtr(mmi, lp, false);
        }
        return CallWindowProc(_prevProc, hwnd, msg, wp, lp);
    }
}
```

- [ ] **Step 4: Replace `MainWindow.xaml`**

```xml
<?xml version="1.0" encoding="utf-8" ?>
<Window
    x:Class="RCMM.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    Title="RCMM">
    <Grid x:Name="RootGrid" Padding="0" Background="{ThemeResource WindowBackground}">
        <Grid.RowDefinitions>
            <RowDefinition Height="36"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <Grid x:Name="AppTitleBar" Grid.Row="0" Background="Transparent" Padding="14,0,140,0">
            <TextBlock Text="RCMM — Right-Click Menu Manager" VerticalAlignment="Center" FontSize="12" Opacity="0.85"/>
        </Grid>

        <Frame x:Name="ContentFrame" Grid.Row="1"/>

        <Grid Grid.Row="2" Padding="20,12,20,14" Background="{ThemeResource FooterBackground}">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <TextBlock x:Name="StatusLabel" Text="0 entries · 0 pending" FontWeight="SemiBold" VerticalAlignment="Center"/>
            <Button Grid.Column="1" x:Name="ApplyButton" Content="Apply (restart Explorer)"
                    Click="ApplyButton_Click" IsEnabled="False"/>
        </Grid>
    </Grid>
</Window>
```

- [ ] **Step 5: Replace `MainWindow.xaml.cs`**

```csharp
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RCMM.Core.Services;
using RCMM.Core.ViewModels;
using RCMM.Util;
using RCMM.Views;
using Windows.UI;

namespace RCMM;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    private Windows.UI.ViewManagement.UISettings? _uiSettings;

    public MainWindow()
    {
        InitializeComponent();

        var registry = new Win32Registry();
        var resolver = new ClsidResolver(registry);
        var scanner = new EntryScanner(
            new ClassicVerbScanner(registry),
            new ClassicShellexScanner(registry, resolver));
        var hide = new HideService(registry);
        ViewModel = new MainViewModel(scanner, hide);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        TryRemoveWindowBorder();
        WindowMinSize.Apply(WinRT.Interop.WindowNative.GetWindowHandle(this), 600, 480);

        HookThemeChange();
        ViewModel.PropertyChanged += OnVmPropertyChanged;
        ViewModel.PendingChanges.CollectionChanged += (_, __) => RefreshFooter();
        ViewModel.Rescan();
        RefreshFooter();

        ContentFrame.Navigate(typeof(LandingPage), ViewModel);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.RequiresExplorerRestart))
            RefreshFooter();
    }

    private void RefreshFooter()
    {
        var total = 0;
        foreach (var scope in new[] {
            RCMM.Core.Models.Scope.Files,
            RCMM.Core.Models.Scope.Folders,
            RCMM.Core.Models.Scope.Drives,
            RCMM.Core.Models.Scope.Background })
        {
            total += ViewModel.GetScope(scope).Entries.Count;
        }
        StatusLabel.Text = $"{total} entries · {ViewModel.PendingChanges.Count} pending";
        ApplyButton.IsEnabled = ViewModel.PendingChanges.Count > 0;
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var needsRestart = ViewModel.RequiresExplorerRestart;
        ViewModel.ApplyPending();
        if (needsRestart) new ExplorerRestart().Restart();
        ViewModel.Rescan();
        RefreshFooter();
    }

    private void TryRemoveWindowBorder()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            uint color = Win32.DWMWA_COLOR_NONE;
            Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_BORDER_COLOR, ref color, sizeof(uint));
        }
        catch { }
    }

    private void HookThemeChange()
    {
        try
        {
            _uiSettings = new Windows.UI.ViewManagement.UISettings();
            _uiSettings.ColorValuesChanged += (s, a) =>
                DispatcherQueue.TryEnqueue(UpdateForCurrentTheme);
            UpdateForCurrentTheme();
        }
        catch { }
    }

    private void UpdateForCurrentTheme()
    {
        if (_uiSettings is null) return;
        var bg = _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
        bool isDark = (bg.R + bg.G + bg.B) < 384;
        RootGrid.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
    }
}
```

NOTE: `LandingPage` doesn't exist yet — the next task creates it. The build will fail here intentionally; do that step in Task 15 immediately before re-running.

- [ ] **Step 6: Commit (build will not succeed until Task 15 lands)**

Defer the commit until Task 15 finishes so we commit a buildable state.

---

### Task 15: `LandingPage` — hub of cards

**Files:**
- Create: `manager/src/RCMM/Views/LandingPage.xaml`
- Create: `manager/src/RCMM/Views/LandingPage.xaml.cs`

- [ ] **Step 1: Create `LandingPage.xaml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<Page
    x:Class="RCMM.Views.LandingPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid Padding="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <TextBlock Text="Scopes" FontSize="20" FontWeight="SemiBold" Margin="0,0,0,12"/>

        <ItemsRepeater Grid.Row="1" x:Name="CardRepeater">
            <ItemsRepeater.Layout>
                <UniformGridLayout MinItemWidth="220" MinItemHeight="100"
                                   MinColumnSpacing="12" MinRowSpacing="12"/>
            </ItemsRepeater.Layout>
            <ItemsRepeater.ItemTemplate>
                <DataTemplate>
                    <Button Padding="16" HorizontalAlignment="Stretch" HorizontalContentAlignment="Left"
                            Click="Card_Click" Tag="{Binding Scope}">
                        <StackPanel>
                            <TextBlock Text="{Binding Title}" FontSize="14" FontWeight="SemiBold"/>
                            <TextBlock Text="{Binding Subtitle}" FontSize="11" Opacity="0.7"/>
                        </StackPanel>
                    </Button>
                </DataTemplate>
            </ItemsRepeater.ItemTemplate>
        </ItemsRepeater>
    </Grid>
</Page>
```

- [ ] **Step 2: Create `LandingPage.xaml.cs`**

```csharp
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RCMM.Core.Models;
using RCMM.Core.ViewModels;

namespace RCMM.Views;

public sealed partial class LandingPage : Page
{
    public sealed record CardItem(Scope Scope, string Title, string Subtitle);

    private MainViewModel _vm = null!;

    public LandingPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _vm = (MainViewModel)e.Parameter;
        var cards = new List<CardItem>();
        foreach (var scope in new[] { Scope.Files, Scope.Folders, Scope.Drives, Scope.Background })
        {
            var list = _vm.GetScope(scope).Entries;
            var hidden = list.Count(r => r.IsHidden);
            cards.Add(new CardItem(scope, ToTitle(scope), $"{hidden} hidden of {list.Count}"));
        }
        CardRepeater.ItemsSource = cards;
    }

    private static string ToTitle(Scope s) => s switch
    {
        Scope.Files       => "Files",
        Scope.Folders     => "Folders",
        Scope.Drives      => "Drives",
        Scope.Background  => "Desktop & folder background",
        _ => s.ToString()
    };

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        var scope = (Scope)btn.Tag;
        Frame.Navigate(typeof(ScopePage), (_vm, scope));
    }
}
```

NOTE: `ScopePage` doesn't exist yet — next task creates it. Don't commit until Task 16.

---

### Task 16: `ScopePage` — drill-down list with toggles

**Files:**
- Create: `manager/src/RCMM/Views/ScopePage.xaml`
- Create: `manager/src/RCMM/Views/ScopePage.xaml.cs`

- [ ] **Step 1: Create `ScopePage.xaml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<Page
    x:Class="RCMM.Views.ScopePage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:vm="using:RCMM.Core.ViewModels">
    <Grid Padding="20,12,20,12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <Grid Grid.Row="0">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>
            <Button x:Name="BackButton" Click="BackButton_Click" Padding="6,2" Background="Transparent" BorderThickness="0">
                <FontIcon Glyph="&#xE72B;" FontSize="14"/>
            </Button>
            <TextBlock Grid.Column="1" x:Name="ScopeTitle" Margin="8,0,0,0" FontSize="20" FontWeight="SemiBold" VerticalAlignment="Center"/>
        </Grid>

        <TextBox Grid.Row="1" x:Name="SearchBox" Margin="0,12,0,8" PlaceholderText="Search…" TextChanged="SearchBox_TextChanged"/>

        <ListView Grid.Row="2" x:Name="EntriesList" SelectionMode="None">
            <ListView.ItemTemplate>
                <DataTemplate x:DataType="vm:EntryRowViewModel">
                    <Grid Padding="6,8" ColumnSpacing="14">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="Auto"/>
                            <ColumnDefinition Width="Auto"/>
                        </Grid.ColumnDefinitions>
                        <StackPanel>
                            <StackPanel Orientation="Horizontal" Spacing="6">
                                <Border CornerRadius="3" Padding="4,1" Background="#22808080"
                                        Visibility="{x:Bind IsBuiltIn, Converter={StaticResource BoolToVisibilityConverter}}">
                                    <TextBlock Text="Built-in" FontSize="10" Opacity="0.8"/>
                                </Border>
                                <TextBlock Text="{x:Bind DisplayName}" FontSize="14"/>
                            </StackPanel>
                            <TextBlock FontSize="11" Opacity="0.6">
                                <Run Text="{x:Bind Source}"/>
                                <Run Text=" · "/>
                                <Run Text="{x:Bind KindLabel}"/>
                            </TextBlock>
                        </StackPanel>
                        <ToggleSwitch Grid.Column="1" IsOn="{x:Bind IsHidden, Mode=TwoWay, Converter={StaticResource InvertBoolConverter}}"
                                      OnContent="" OffContent=""/>
                    </Grid>
                </DataTemplate>
            </ListView.ItemTemplate>
        </ListView>
    </Grid>
</Page>
```

NOTE: The toggle visually represents "visible" (on = entry is visible in menu, off = hidden), but our model stores `IsHidden`. We invert via a converter.

- [ ] **Step 2: Add `InvertBoolConverter` and register it**

At the top of `App.xaml`, add the namespace:

```
xmlns:local="using:RCMM"
```

(Converter resource registration happens in Step 2.)

Create `manager/src/RCMM/InvertBoolConverter.cs`:

```csharp
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace RCMM;

public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, System.Type t, object p, string l) => !(bool)value;
    public object ConvertBack(object value, System.Type t, object p, string l) => !(bool)value;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, System.Type t, object p, string l)
        => (bool)value ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, System.Type t, object p, string l)
        => (Visibility)value == Visibility.Visible;
}
```

And register both in `App.xaml` (inside `<ResourceDictionary>` but outside `MergedDictionaries`):

```xml
<local:InvertBoolConverter x:Key="InvertBoolConverter"/>
<local:BoolToVisibilityConverter x:Key="BoolToVisibilityConverter"/>
```

- [ ] **Step 3: Create `ScopePage.xaml.cs`**

```csharp
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RCMM.Core.Models;
using RCMM.Core.ViewModels;

namespace RCMM.Views;

public sealed partial class ScopePage : Page
{
    private MainViewModel _vm = null!;
    private Scope _scope;

    public ScopePage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var (vm, scope) = ((MainViewModel, Scope))e.Parameter;
        _vm = vm;
        _scope = scope;
        ScopeTitle.Text = scope switch
        {
            Scope.Files       => "Files",
            Scope.Folders     => "Folders",
            Scope.Drives      => "Drives",
            Scope.Background  => "Desktop & folder background",
            _ => scope.ToString()
        };
        EntriesList.ItemsSource = vm.GetScope(scope).Entries;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var needle = SearchBox.Text?.Trim() ?? "";
        if (needle.Length == 0)
        {
            EntriesList.ItemsSource = _vm.GetScope(_scope).Entries;
            return;
        }
        EntriesList.ItemsSource = _vm.GetScope(_scope).Entries
            .Where(r => r.DisplayName.Contains(needle, System.StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
```

- [ ] **Step 4: Build**

```powershell
dotnet build manager/RCMM.sln
```

Expected: build succeeds across all three projects.

- [ ] **Step 5: Commit Tasks 14-16 together**

```powershell
git add manager
git commit -m "ui: window shell, landing hub, scope drill-down, apply footer"
```

---

### Task 17: Manual smoke test on real registry

This is the moment of truth. Run the app and confirm it correctly enumerates and toggles your real classic-menu entries.

- [ ] **Step 1: Run the app**

```powershell
dotnet run --project manager/src/RCMM/RCMM.csproj
```

Approve the UAC prompt. The landing page should appear with four cards (Files, Folders, Drives, Desktop & folder background) and accurate counts.

- [ ] **Step 2: Drill into Files**

Click the Files card. You should see a long list including entries like WinRAR, VLC, Open with Visual Studio, Git GUI, Git Bash, Open in Terminal, etc.

- [ ] **Step 3: Toggle one safe item off**

Pick a non-Windows third-party item (e.g., a VLC entry or a Git Bash entry). Toggle its switch off (entry → hidden). The footer should now show "1 pending" and the Apply button should enable.

- [ ] **Step 4: Apply**

Click Apply. Explorer flashes; the app rescans. Open File Explorer, right-click a file, choose "Show more options" — the toggled entry should be gone.

- [ ] **Step 5: Re-enable**

Find the same entry, toggle it back on, Apply. Verify in Explorer that it reappears.

- [ ] **Step 6: If anything goes wrong**

If a toggle doesn't take effect: check that the entry was a `ShellExtension` (those require restart) and that `RequiresExplorerRestart` was true. Check `%APPDATA%\RCMM\` — we haven't built logging yet, but `config.json` should reflect the current state after the next plan iteration.

- [ ] **Step 7: Commit anything you find (no plan changes here)**

If you find a bug, write a regression test in `RCMM.Tests` first, then fix.

---

### Task 18: Tag v0.1 and write README

**Files:**
- Modify: `RCMM/README.md` (create)
- Tag: `v0.1.0`

- [ ] **Step 1: Create README.md**

```markdown
# RCMM — Right-Click Menu Manager

A Windows 11 utility for curating your right-click menu. v0.1 (this build) covers
hiding entries in the classic ("Show more options") menu.

## Build

Requires .NET 8 SDK and Windows App SDK.

```powershell
dotnet build manager/RCMM.sln
dotnet run --project manager/src/RCMM/RCMM.csproj
```

## Status

- [x] Foundation + classic-menu hide/unhide (Plan 1)
- [ ] Modern Win11 menu hide (Plan 2)
- [ ] Add custom items (Plan 3)
- [ ] Backup snapshot + Undo all (Plan 4)
```

- [ ] **Step 2: Commit and tag**

```powershell
git add RCMM/README.md
git commit -m "docs: readme for v0.1"
git tag v0.1.0
```

---

## Done criteria

- `dotnet test manager/test/RCMM.Tests` reports all green (33+ tests).
- App launches with UAC, displays the landing hub, drills into each of four scopes, lists real entries from the user's machine, toggles work, Apply triggers a clean Explorer restart for shellex changes, and re-running the app reflects the previous state.
- No HKLM writes occur (verified by spot-check: `reg query HKLM\... /s` before and after toggling a verb).
- README and v0.1.0 tag in place.
