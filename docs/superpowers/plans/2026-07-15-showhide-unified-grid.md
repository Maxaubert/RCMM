# Unified Show/Hide Grid Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Show/Hide two-card landing with one unified, filterable, grid-laid-out entry list, and align the title bar settings gear with the system caption buttons.

**Architecture:** A pure filter predicate goes into RCMM.Core (`EntryListFilter`) so chip/search composition is unit-testable. `ScopePage` becomes the single Show/Hide destination: chips (origin + visibility) drive page-local state, the `ListView` becomes an adaptive-column `GridView`, and `ShowHidePage` plus the `ListFilter` navigation plumbing are deleted. The gear fix is one `TitleBarHeightOption.Tall` line so the system caption strip matches the 48px custom bar.

**Tech Stack:** .NET 8, WinUI 3 (Windows App SDK), xUnit. Spec: `docs/superpowers/specs/2026-07-15-showhide-unified-grid-design.md`.

## Global Constraints

- No external NuGet dependencies beyond Windows App SDK / .NET BCL.
- C# file-scoped namespaces, `sealed` by default, records for value types. Comments explain **why**, not what.
- Presentation only: do NOT touch discovery, rename, dedupe, or `MainViewModel.AllEntries` (the step-9 filter). `EntryRowViewModel` and `MenuEntry` are read, never modified.
- Filters use only existing flags: `IsBuiltIn` (origin), `IsHidden` (visibility), `DisplayName` (search). No `Source` filter, no curated categories.
- Tile parity: every tile shows icon, display name, source, Item/Submenu badge, hide `ToggleSwitch`; tap-anywhere toggles (except on the switch), non-hideable entries show "locked" and a disabled switch, exactly as today's rows.
- Visual language: dark flat, `AppSurface`/`AppBorder`/`AppText` theme resources, lime accent `#d4ff3a`, `ChipButton` style from App.xaml for chips.
- Build: `dotnet build manager/RCMM.sln`. Tests: `dotnet test manager/RCMM.sln`. Baseline: 271 passed / 1 skipped on `main` (279 if PR #25 merged first — record the number you see before starting and compare against it).
- Work lands via GitHub issue + branch `feat/showhide-unified-grid` + PR (`gh` CLI).

---

### Task 1: GitHub issue, branch, and the Core filter predicate

**Files:**
- Create: `manager/src/RCMM.Core/ViewModels/EntryListFilter.cs`
- Test: `manager/test/RCMM.Tests/EntryListFilterTests.cs`

**Interfaces:**
- Consumes: `EntryRowViewModel` (existing: `DisplayName`, `IsBuiltIn`, `IsHidden` properties), `MenuEntry` (existing record; required members `Id`, `DisplayName`, `HideTargets`).
- Produces: `enum OriginFilter { All, Apps, Windows }`, `enum VisibilityFilter { All, Visible, Hidden }`, and `static bool EntryListFilter.Matches(EntryRowViewModel row, OriginFilter origin, VisibilityFilter visibility, string? search)` — Task 2's code-behind calls exactly this.

- [ ] **Step 1: Create the GitHub issue and branch**

```bash
gh issue create --title "Unified Show/Hide grid + title bar gear alignment" --body "Merge the Show/Hide App/Windows two-card split into one unified list with filter chips (All|Apps|Windows and All|Visible|Hidden, driven by IsBuiltIn/IsHidden), rendered as a responsive multi-column grid. Delete ShowHidePage and the ListFilter nav plumbing. Also align the title bar settings gear with the system caption buttons (TitleBarHeightOption.Tall). Spec: docs/superpowers/specs/2026-07-15-showhide-unified-grid-design.md"
git checkout -b feat/showhide-unified-grid
```

(If the controller pre-created the branch/worktree, skip `git checkout -b`.) Note the issue number for Task 3's PR.

- [ ] **Step 2: Write the failing tests**

Create `manager/test/RCMM.Tests/EntryListFilterTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.ViewModels;

namespace RCMM.Tests;

public class EntryListFilterTests
{
    private static EntryRowViewModel Row(string name, bool builtIn = false, bool hidden = false)
        => new(new MenuEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = name,
            HideTargets = new List<HideTarget>(),
            IsBuiltIn = builtIn,
            IsHidden = hidden,
        });

    [Theory]
    [InlineData(OriginFilter.All, false, true)]
    [InlineData(OriginFilter.All, true, true)]
    [InlineData(OriginFilter.Apps, false, true)]
    [InlineData(OriginFilter.Apps, true, false)]
    [InlineData(OriginFilter.Windows, false, false)]
    [InlineData(OriginFilter.Windows, true, true)]
    public void Origin_filter_uses_IsBuiltIn(OriginFilter origin, bool isBuiltIn, bool expected)
    {
        var row = Row("7-Zip", builtIn: isBuiltIn);
        Assert.Equal(expected, EntryListFilter.Matches(row, origin, VisibilityFilter.All, null));
    }

    [Theory]
    [InlineData(VisibilityFilter.All, false, true)]
    [InlineData(VisibilityFilter.All, true, true)]
    [InlineData(VisibilityFilter.Visible, false, true)]
    [InlineData(VisibilityFilter.Visible, true, false)]
    [InlineData(VisibilityFilter.Hidden, false, false)]
    [InlineData(VisibilityFilter.Hidden, true, true)]
    public void Visibility_filter_uses_IsHidden(VisibilityFilter visibility, bool isHidden, bool expected)
    {
        var row = Row("7-Zip", hidden: isHidden);
        Assert.Equal(expected, EntryListFilter.Matches(row, OriginFilter.All, visibility, null));
    }

    [Theory]
    [InlineData("zip", true)]     // case-insensitive substring
    [InlineData("7-ZIP", true)]
    [InlineData("defender", false)]
    [InlineData(null, true)]      // null/empty/whitespace = no search filter
    [InlineData("", true)]
    [InlineData("   ", true)]
    public void Search_is_case_insensitive_substring_on_DisplayName(string? needle, bool expected)
    {
        var row = Row("7-Zip");
        Assert.Equal(expected, EntryListFilter.Matches(row, OriginFilter.All, VisibilityFilter.All, needle));
    }

    [Fact]
    public void Filters_compose_with_AND_semantics()
    {
        var hiddenApp = Row("Open with Code", builtIn: false, hidden: true);
        Assert.True(EntryListFilter.Matches(hiddenApp, OriginFilter.Apps, VisibilityFilter.Hidden, "code"));
        Assert.False(EntryListFilter.Matches(hiddenApp, OriginFilter.Windows, VisibilityFilter.Hidden, "code"));
        Assert.False(EntryListFilter.Matches(hiddenApp, OriginFilter.Apps, VisibilityFilter.Visible, "code"));
        Assert.False(EntryListFilter.Matches(hiddenApp, OriginFilter.Apps, VisibilityFilter.Hidden, "defender"));
    }
}
```

- [ ] **Step 3: Run the tests, verify they fail to compile**

Run: `dotnet test manager/RCMM.sln --filter "FullyQualifiedName~EntryListFilterTests" -v minimal`
Expected: BUILD FAILURE — `OriginFilter`, `VisibilityFilter`, `EntryListFilter` do not exist.

- [ ] **Step 4: Implement the predicate**

Create `manager/src/RCMM.Core/ViewModels/EntryListFilter.cs`:

```csharp
using System;

namespace RCMM.Core.ViewModels;

public enum OriginFilter { All, Apps, Windows }
public enum VisibilityFilter { All, Visible, Hidden }

/// <summary>
/// Pure predicate behind the unified Show/Hide list's chip row + search box.
/// Lives in Core rather than the page so the chip/search composition is
/// unit-testable without WinUI.
/// </summary>
public static class EntryListFilter
{
    public static bool Matches(EntryRowViewModel row, OriginFilter origin,
                               VisibilityFilter visibility, string? search)
    {
        if (origin == OriginFilter.Apps && row.IsBuiltIn) return false;
        if (origin == OriginFilter.Windows && !row.IsBuiltIn) return false;
        if (visibility == VisibilityFilter.Visible && row.IsHidden) return false;
        if (visibility == VisibilityFilter.Hidden && !row.IsHidden) return false;
        var needle = search?.Trim();
        if (!string.IsNullOrEmpty(needle)
            && !row.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }
}
```

- [ ] **Step 5: Run the tests, verify they pass**

Run: `dotnet test manager/RCMM.sln --filter "FullyQualifiedName~EntryListFilterTests" -v minimal`
Expected: 19/19 PASS (6 + 6 + 6 theory cases + 1 fact — xUnit counts each InlineData row).

- [ ] **Step 6: Run the full suite**

Run: `dotnet test manager/RCMM.sln -v minimal`
Expected: baseline + 19, 0 failed, 1 skipped.

- [ ] **Step 7: Commit**

```bash
git add manager/src/RCMM.Core/ViewModels/EntryListFilter.cs manager/test/RCMM.Tests/EntryListFilterTests.cs
git commit -m "feat: testable filter predicate for the unified Show/Hide list

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: ScopePage becomes the unified grid page; ShowHidePage dies

**Files:**
- Modify: `manager/src/RCMM/Views/ScopePage.xaml` (full-file replacement below)
- Modify: `manager/src/RCMM/Views/ScopePage.xaml.cs` (full-file replacement below)
- Modify: `manager/src/RCMM/Views/NavArgs.cs` (drop `ListFilter`)
- Modify: `manager/src/RCMM/Views/LandingPage.xaml.cs:111` (retarget navigation)
- Delete: `manager/src/RCMM/Views/ShowHidePage.xaml`, `manager/src/RCMM/Views/ShowHidePage.xaml.cs`

**Interfaces:**
- Consumes: `EntryListFilter.Matches(EntryRowViewModel, OriginFilter, VisibilityFilter, string?)` from Task 1; `ChipButton` style (App.xaml, exists); `AppSurface`/`AppBorder`/`AppAccent` theme resources; `MainViewModel.AllEntries` + `RescanComplete` (existing).
- Produces: `NavArgs` record without the `Filter` parameter — `public sealed record NavArgs(MainViewModel ViewModel);`. Anything constructing `NavArgs(vm, filter)` must lose the second argument (only the deleted `ShowHidePage` does).

- [ ] **Step 1: Replace `ScopePage.xaml`**

Full new content. The heading becomes "Show / hide"; row 1 holds the two chip groups (left) and the search box (right); the list is a `GridView` whose `ItemsWrapGrid.ItemWidth` is set from code on size changes. The TextBox resource block and ToggleSwitch resource block are carried over verbatim from the current file. Tiles are rounded `AppSurface` cards; the old ListView row internals (icon / name / BUILT-IN badge / source · kind line / locked label / toggle) move into the tile unchanged.

```xml
<?xml version="1.0" encoding="utf-8"?>
<Page
    x:Class="RCMM.Views.ScopePage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:vm="using:RCMM.Core.ViewModels">
    <Grid Padding="48,28,48,12" RowSpacing="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- Heading -->
        <TextBlock Grid.Row="0" Text="Show / hide" FontSize="34" FontWeight="Medium"
                   Foreground="{ThemeResource AppText}" CharacterSpacing="-15"
                   HorizontalAlignment="Left"/>

        <!-- Filter bar: origin chips · visibility chips · search -->
        <Grid Grid.Row="1" ColumnSpacing="16">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <StackPanel Grid.Column="0" Orientation="Horizontal" Spacing="8" VerticalAlignment="Center">
                <Button x:Name="ChipOriginAll" Tag="all"     Content="All" Click="OriginChip_Click" Style="{StaticResource ChipButton}"/>
                <Button x:Name="ChipApps"      Tag="apps"    Click="OriginChip_Click" Style="{StaticResource ChipButton}"/>
                <Button x:Name="ChipWindows"   Tag="windows" Click="OriginChip_Click" Style="{StaticResource ChipButton}"/>
                <Border Width="1" Height="20" Background="{ThemeResource AppBorder}" Margin="6,0" VerticalAlignment="Center"/>
                <Button x:Name="ChipVisAll"   Tag="all"     Content="All"     Click="VisibilityChip_Click" Style="{StaticResource ChipButton}"/>
                <Button x:Name="ChipVisible"  Tag="visible" Content="Visible" Click="VisibilityChip_Click" Style="{StaticResource ChipButton}"/>
                <Button x:Name="ChipHidden"   Tag="hidden"  Content="Hidden"  Click="VisibilityChip_Click" Style="{StaticResource ChipButton}"/>
            </StackPanel>
            <TextBox Grid.Column="1" x:Name="SearchBox" Width="260"
                     PlaceholderText="Search…" TextChanged="SearchBox_TextChanged"
                     Background="{ThemeResource AppSurface}" BorderBrush="{ThemeResource AppBorder}"
                     CornerRadius="10" Padding="10,8" FontSize="13">
                <TextBox.Resources>
                    <SolidColorBrush x:Key="TextControlBackgroundPointerOver"  Color="#141418"/>
                    <SolidColorBrush x:Key="TextControlBackgroundFocused"      Color="#141418"/>
                    <SolidColorBrush x:Key="TextControlBackgroundDisabled"     Color="#141418"/>
                    <SolidColorBrush x:Key="TextControlBorderBrushPointerOver" Color="#26262c"/>
                    <SolidColorBrush x:Key="TextControlBorderBrushFocused"     Color="#26262c"/>
                    <SolidColorBrush x:Key="TextControlBorderBrushDisabled"    Color="#26262c"/>
                    <Thickness x:Key="TextControlBorderThemeThicknessFocused">1</Thickness>
                </TextBox.Resources>
            </TextBox>
        </Grid>

        <!-- Grid of entry tiles. ItemWidth is computed in code-behind from the
             viewport width (min tile ~360px) so the column count adapts. -->
        <GridView Grid.Row="2" x:Name="EntriesGrid" SelectionMode="None"
                  Background="Transparent" Padding="0" IsItemClickEnabled="False"
                  SizeChanged="EntriesGrid_SizeChanged">
            <GridView.ItemsPanel>
                <ItemsPanelTemplate>
                    <ItemsWrapGrid Orientation="Horizontal"/>
                </ItemsPanelTemplate>
            </GridView.ItemsPanel>
            <GridView.ItemContainerStyle>
                <Style TargetType="GridViewItem">
                    <Setter Property="Padding" Value="0"/>
                    <Setter Property="Margin" Value="0"/>
                    <Setter Property="MinWidth" Value="0"/>
                    <Setter Property="MinHeight" Value="0"/>
                    <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
                    <Setter Property="VerticalContentAlignment" Value="Stretch"/>
                    <!-- Bare presenter: the tile Border owns hover/pressed visuals,
                         so the default GridViewItem chrome (gray reveal, selection
                         border) would fight it. -->
                    <Setter Property="Template">
                        <Setter.Value>
                            <ControlTemplate TargetType="GridViewItem">
                                <ContentPresenter Content="{TemplateBinding Content}"
                                                  ContentTemplate="{TemplateBinding ContentTemplate}"
                                                  HorizontalContentAlignment="Stretch"
                                                  VerticalContentAlignment="Stretch"/>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
            </GridView.ItemContainerStyle>
            <GridView.ItemTemplate>
                <DataTemplate x:DataType="vm:EntryRowViewModel">
                    <Border Margin="0,0,10,10" Padding="14,12" CornerRadius="10"
                            Background="{ThemeResource AppSurface}"
                            BorderBrush="{ThemeResource AppBorder}" BorderThickness="1"
                            PointerEntered="Row_PointerEntered"
                            PointerExited="Row_PointerExited"
                            Tapped="Row_Tapped">
                        <Grid ColumnSpacing="14">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="32"/>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="Auto"/>
                            </Grid.ColumnDefinitions>
                            <Image Width="22" Height="22"
                                   Source="{x:Bind Icon, Converter={StaticResource ObjectToImageSourceConverter}, Mode=OneWay}"
                                   VerticalAlignment="Center" HorizontalAlignment="Center"/>
                            <StackPanel Grid.Column="1" Spacing="3" VerticalAlignment="Center">
                                <TextBlock Text="{x:Bind DisplayName}" FontSize="14"
                                           TextTrimming="CharacterEllipsis"
                                           Foreground="{ThemeResource AppText}"/>
                                <StackPanel Orientation="Horizontal" Spacing="8">
                                    <Border Padding="4,1" CornerRadius="3"
                                            Background="#1a2810" BorderBrush="#3d5a1d" BorderThickness="1"
                                            Visibility="{x:Bind IsBuiltIn, Converter={StaticResource BoolToVisibilityConverter}}">
                                        <TextBlock Text="BUILT-IN" FontSize="9"
                                                   FontFamily="Consolas, Cascadia Code, monospace"
                                                   Foreground="#92bd4c" CharacterSpacing="100"/>
                                    </Border>
                                    <TextBlock FontSize="11"
                                               FontFamily="Consolas, Cascadia Code, monospace"
                                               Foreground="{ThemeResource AppTextDim}"
                                               TextTrimming="CharacterEllipsis">
                                        <Run Text="{x:Bind Source}"/>
                                        <Run Text=" · "/>
                                        <Run Text="{x:Bind KindLabel}"/>
                                    </TextBlock>
                                </StackPanel>
                            </StackPanel>
                            <StackPanel Grid.Column="2" Orientation="Horizontal" Spacing="8" VerticalAlignment="Center">
                                <TextBlock Text="locked" FontSize="10"
                                           FontFamily="Consolas, Cascadia Code, monospace"
                                           Foreground="{ThemeResource AppTextDim}"
                                           VerticalAlignment="Center"
                                           Visibility="{x:Bind CanHide, Converter={StaticResource InvertBoolToVisibilityConverter}}"/>
                                <ToggleSwitch IsOn="{x:Bind IsHidden, Mode=TwoWay, Converter={StaticResource InvertBoolConverter}}"
                                              IsEnabled="{x:Bind CanHide}"
                                              OnContent="" OffContent="" MinWidth="0">
                                    <ToggleSwitch.Resources>
                                        <SolidColorBrush x:Key="ToggleSwitchFillOn"                Color="#d4ff3a"/>
                                        <SolidColorBrush x:Key="ToggleSwitchFillOnPointerOver"     Color="#e5ff5a"/>
                                        <SolidColorBrush x:Key="ToggleSwitchFillOnPressed"         Color="#c2eb28"/>
                                        <SolidColorBrush x:Key="ToggleSwitchStrokeOn"              Color="#d4ff3a"/>
                                        <SolidColorBrush x:Key="ToggleSwitchStrokeOnPointerOver"   Color="#d4ff3a"/>
                                        <SolidColorBrush x:Key="ToggleSwitchStrokeOnPressed"       Color="#d4ff3a"/>
                                        <SolidColorBrush x:Key="ToggleSwitchKnobFillOn"            Color="#0a0a0a"/>
                                        <SolidColorBrush x:Key="ToggleSwitchKnobFillOnPointerOver" Color="#0a0a0a"/>
                                        <SolidColorBrush x:Key="ToggleSwitchKnobFillOnPressed"     Color="#0a0a0a"/>
                                        <SolidColorBrush x:Key="AccentFillColorDefaultBrush"       Color="#d4ff3a"/>
                                        <SolidColorBrush x:Key="AccentFillColorSecondaryBrush"     Color="#e5ff5a"/>
                                        <SolidColorBrush x:Key="AccentFillColorTertiaryBrush"      Color="#c2eb28"/>
                                        <SolidColorBrush x:Key="TextOnAccentFillColorPrimaryBrush" Color="#0a0a0a"/>
                                    </ToggleSwitch.Resources>
                                </ToggleSwitch>
                            </StackPanel>
                        </Grid>
                    </Border>
                </DataTemplate>
            </GridView.ItemTemplate>
        </GridView>
    </Grid>
</Page>
```

- [ ] **Step 2: Replace `ScopePage.xaml.cs`**

```csharp
using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using RCMM.Core.ViewModels;
using Windows.UI;

namespace RCMM.Views;

public sealed partial class ScopePage : Page
{
    private MainViewModel _vm = null!;
    private OriginFilter _origin = OriginFilter.All;
    private VisibilityFilter _visibility = VisibilityFilter.All;

    // Faint lime-tinted hover (~6% alpha) to match the card glow
    private static readonly SolidColorBrush HoverBrush = new(Color.FromArgb(0x10, 0xd4, 0xff, 0x3a));

    public ScopePage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var args = (NavArgs)e.Parameter;
        _vm = args.ViewModel;
        _vm.RescanComplete += OnRescanComplete;
        ApplyChipStyles();
        RebuildList();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_vm != null) _vm.RescanComplete -= OnRescanComplete;
    }

    private void OnRescanComplete()
    {
        DispatcherQueue.TryEnqueue(RebuildList);
    }

    private void OriginChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        _origin = tag switch
        {
            "apps"    => OriginFilter.Apps,
            "windows" => OriginFilter.Windows,
            _         => OriginFilter.All,
        };
        ApplyChipStyles();
        RebuildList();
    }

    private void VisibilityChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        _visibility = tag switch
        {
            "visible" => VisibilityFilter.Visible,
            "hidden"  => VisibilityFilter.Hidden,
            _         => VisibilityFilter.All,
        };
        ApplyChipStyles();
        RebuildList();
    }

    /// <summary>Active chip gets the lime accent; the rest sit on the surface
    /// color with a subtle border. Same pattern as TemplatesPage.ApplyChipStyles —
    /// geometry is owned by ChipButton in App.xaml, this only flips colors.</summary>
    private void ApplyChipStyles()
    {
        foreach (var (chip, active) in new[]
        {
            (ChipOriginAll, _origin == OriginFilter.All),
            (ChipApps,      _origin == OriginFilter.Apps),
            (ChipWindows,   _origin == OriginFilter.Windows),
            (ChipVisAll,    _visibility == VisibilityFilter.All),
            (ChipVisible,   _visibility == VisibilityFilter.Visible),
            (ChipHidden,    _visibility == VisibilityFilter.Hidden),
        })
        {
            chip.Background = (Brush)Application.Current.Resources[active ? "AppAccent" : "AppSurface"];
            chip.BorderBrush = (Brush)Application.Current.Resources[active ? "AppAccent" : "AppBorder"];
            chip.Foreground = active
                ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 10, 10, 10))
                : (Brush)Application.Current.Resources["AppText"];
        }
    }

    /// <summary>
    /// Re-filter and re-source the grid. Deliberately NOT wired to per-row
    /// IsHidden changes: with the Visible/Hidden chip active, a just-toggled
    /// tile stays put until the next rebuild (chip click, search, rescan)
    /// instead of vanishing under the cursor mid-toggle.
    /// </summary>
    private void RebuildList()
    {
        int apps = 0, windows = 0;
        foreach (var r in _vm.AllEntries) { if (r.IsBuiltIn) windows++; else apps++; }
        ChipApps.Content = $"Apps ({apps})";
        ChipWindows.Content = $"Windows ({windows})";

        var needle = SearchBox.Text;
        EntriesGrid.ItemsSource = _vm.AllEntries
            .Where(r => EntryListFilter.Matches(r, _origin, _visibility, needle))
            .ToList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildList();

    /// <summary>Adaptive columns: as many ~360px-minimum tiles as fit, tiles
    /// stretch to share the row exactly. One column below 720px.</summary>
    private void EntriesGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (EntriesGrid.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        const double minTile = 360;
        double width = e.NewSize.Width;
        if (width <= 0) return;
        int columns = Math.Max(1, (int)(width / minTile));
        panel.ItemWidth = Math.Floor(width / columns);
    }

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Background = HoverBrush;
    }

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Tiles rest on AppSurface (not transparent like the old full-width rows).
        if (sender is Border b) b.Background = (Brush)Application.Current.Resources["AppSurface"];
    }

    private void Row_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // If the tap came from inside the ToggleSwitch, let it handle itself.
        if (e.OriginalSource is DependencyObject src && FindAncestor<ToggleSwitch>(src) != null) return;
        if (sender is FrameworkElement fe && fe.DataContext is EntryRowViewModel vm && vm.CanHide)
        {
            vm.IsHidden = !vm.IsHidden;
        }
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}
```

- [ ] **Step 3: Drop `ListFilter` from `NavArgs.cs`**

Replace the file body so it reads:

```csharp
using RCMM.Core.ViewModels;

namespace RCMM.Views;

/// <summary>
/// Frame.Navigate parameter — every page in the layout takes the same
/// ViewModel reference. (The old ListFilter went away with ShowHidePage:
/// the unified list filters via page-local chips, not navigation args.)
/// </summary>
public sealed record NavArgs(MainViewModel ViewModel);
```

- [ ] **Step 4: Retarget the landing card and delete ShowHidePage**

In `manager/src/RCMM/Views/LandingPage.xaml.cs` line 111, change:

```csharp
        Frame.Navigate(typeof(ShowHidePage), _args);
```
to
```csharp
        Frame.Navigate(typeof(ScopePage), _args);
```

Then delete both files:

```bash
git rm manager/src/RCMM/Views/ShowHidePage.xaml manager/src/RCMM/Views/ShowHidePage.xaml.cs
```

(XAML pages are auto-globbed by the SDK-style project — no .csproj edit needed.)

- [ ] **Step 5: Build and check for stragglers**

Run: `dotnet build manager/RCMM.sln`
Expected: SUCCESS, 0 errors. Then confirm nothing else references the deleted page or enum:
`grep -rn "ShowHidePage\|ListFilter" manager/src --include="*.cs" --include="*.xaml"` (excluding `obj/`/`bin/`) — expected: no hits.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test manager/RCMM.sln -v minimal`
Expected: same totals as Task 1 Step 6, 0 failed.

- [ ] **Step 7: Commit**

```bash
git add -A manager
git commit -m "feat: unified Show/Hide grid with origin + visibility chips; delete ShowHidePage

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: Title bar gear alignment, docs, verification, PR

**Files:**
- Modify: `manager/src/RCMM/MainWindow.xaml.cs` (constructor, right after `SetTitleBar(AppTitleBar);` at ~line 60)
- Modify: `CLAUDE.md` (UI direction line)

**Interfaces:**
- Consumes: Tasks 1-2 merged on the branch; issue number from Task 1 Step 1.
- Produces: the PR. Nothing downstream.

- [ ] **Step 1: Make the system caption strip match the 48px bar**

In `manager/src/RCMM/MainWindow.xaml.cs`, immediately after `SetTitleBar(AppTitleBar);`, add:

```csharp
        // The system caption buttons default to the compact (~32px) strip while
        // AppTitleBar is 48px, so our title-bar buttons — vertically centered in
        // the row — sat visibly LOWER than minimize/maximize/close. Tall makes
        // the system strip 48px too, giving both sets the same vertical center.
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
```

- [ ] **Step 2: Update the stale CLAUDE.md line**

In `CLAUDE.md`, the **UI direction** section reads "Donut chart on the landing page, App / Windows split on the show-hide page." Replace that sentence with:

"Donut chart on the landing page, one unified filterable grid (origin + visibility chips) on the show-hide page."

- [ ] **Step 3: Build + full suite**

Run: `dotnet build manager/RCMM.sln && dotnet test manager/RCMM.sln -v minimal`
Expected: build clean; same test totals as Task 2 Step 6.

- [ ] **Step 4: Verify visually**

Follow the `build-release` skill's run/screenshot flow (`scripts\screenshot-rcmm.ps1`). Checks:

1. Landing "Show / hide" card opens the unified page directly (no two-card page anywhere).
2. Chip row shows All | Apps (n) | Windows (n) | divider | All | Visible | Hidden; active chips are lime; counts are plausible (n_apps + n_windows = total entries).
3. Entries render as bordered card tiles in 2+ columns at default window width and reflow to 1 column when the window is narrow.
4. Gear icon's vertical center matches minimize/maximize/close (screenshot the top strip).
5. Toggling a tile still works (tap anywhere), "locked" entries stay disabled.

Screenshot evidence saved for the PR. Live click-driving may be unavailable in this environment — verify what's verifiable via screenshots (1-4 are all visual) and report check 5 honestly if it can't be driven.

- [ ] **Step 5: Commit, push, PR**

```bash
git add manager/src/RCMM/MainWindow.xaml.cs CLAUDE.md
git commit -m "fix: align title bar buttons with system caption strip (Tall height); docs

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
git push -u origin feat/showhide-unified-grid
gh pr create --title "Unified Show/Hide grid + title bar gear alignment" --body "Closes #<issue-number-from-Task-1>.

- Show/Hide is now one page: ScopePage with All|Apps|Windows and All|Visible|Hidden chips (IsBuiltIn/IsHidden driven) + search, rendered as an adaptive multi-column card grid (~360px min tile).
- ShowHidePage and the ListFilter nav plumbing are deleted; the landing card navigates straight to the unified page.
- Title bar: TitleBarHeightOption.Tall aligns the settings gear (and back/home) with the system caption buttons.
- New EntryListFilter predicate in Core with 19 unit tests.

Spec: docs/superpowers/specs/2026-07-15-showhide-unified-grid-design.md

🤖 Generated with [Claude Code](https://claude.com/claude-code)"
```

Expected: PR opens referencing the issue. (If the controller defers the push/PR for a final review, skip this step's push/PR on their instruction.)
