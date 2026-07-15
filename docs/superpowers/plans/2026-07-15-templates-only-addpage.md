# Templates-only Add Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Trim RCMM's "Add to menu" page to a templates-only workflow — entries come exclusively from the built-in template catalogue; users structure them (drag order, folders/submenus, name, icon) and nothing else.

**Architecture:** Three surgical changes: (1) a schema v5 migration in `AdditionStore` that drops hand-authored entries (`SourceTemplateId == null`) on load; (2) removal of the "+ New entry" toolbar path and all non-structural editor fields (Command, Working dir, Run mode, Terminal, Scope, File types) from `AddPage.xaml` + code-behind; (3) doc updates (CLAUDE.md scope, ROADMAP §3). The `AdditionEntry` model keeps all its fields — `AdditionApplier` and the template clone path still need them; they just stop being user-editable.

**Tech Stack:** .NET 8, WinUI 3 (Windows App SDK), xUnit. Spec: `docs/superpowers/specs/2026-07-15-templates-only-addpage-design.md`.

## Global Constraints

- No external NuGet dependencies beyond Windows App SDK / .NET BCL.
- C# file-scoped namespaces, `sealed` by default, records for value types.
- Comments explain **why**, not what.
- No em-dashes in any text/copy; use en-dashes or rephrase.
- Work lands via GitHub issue + `feat/` branch + PR referencing the issue (`gh` CLI).
- Build: `dotnet build manager/RCMM.sln`. Tests: `dotnet test manager/RCMM.sln`.
- Safety invariant: template-added entries are stamped via `TemplateUpdateService.Stamp` (sets `SourceTemplateId`) in `TemplatesPage.AddTemplate` — verified; the v5 drop only ever removes hand-authored entries.

---

### Task 1: GitHub issue, branch, and schema v5 migration

**Files:**
- Modify: `manager/src/RCMM.Core/Models/AdditionState.cs`
- Modify: `manager/src/RCMM.Core/Services/AdditionStore.cs` (method `MigrateIfNeeded`, lines ~68-115)
- Test: `manager/test/RCMM.Tests/AdditionStoreTests.cs`

**Interfaces:**
- Consumes: `AdditionEntry.SourceTemplateId` (existing, null = hand-authored), `AdditionState` record, `Log.Info(Cat, …)`.
- Produces: `AdditionState.CurrentSchemaVersion == 5`; `AdditionStore.Load()`/`MigrateIfNeeded` that drop hand-authored entries from pre-v5 files. No signature changes — later tasks rely only on this behavior.

- [ ] **Step 1: Create the GitHub issue and branch**

```bash
gh issue create --title "Trim Add page to templates-only" --body "Remove the ad-hoc '+ New entry' path and non-structural editor fields (Command, Working dir, Run mode, Terminal, Scope, File types). Add page becomes: add from template catalogue + structure (order, folders, name, icon). Schema v5 migration drops hand-authored entries on load. Spec: docs/superpowers/specs/2026-07-15-templates-only-addpage-design.md"
git checkout -b feat/templates-only-addpage
```

Note the issue number printed by `gh issue create` — the PR body in Task 3 references it.

- [ ] **Step 2: Write the failing migration tests**

Append to `manager/test/RCMM.Tests/AdditionStoreTests.cs` (inside the existing `AdditionStoreTests` class, matching its temp-file style):

```csharp
    [Fact]
    public void Load_migrates_v4_to_v5_dropping_hand_authored_entries()
    {
        // e1 has no sourceTemplateId (hand-authored, pre-trim) -> dropped.
        // e2 is template-derived -> survives with hidden/folder/order intact.
        var path = TempFile();
        try
        {
            File.WriteAllText(path,
                @"{""schemaVersion"":4,
                   ""folders"":[{""id"":""f1"",""name"":""Dev tools""}],
                   ""entries"":[
                     {""id"":""e1"",""name"":""My own thing"",""command"":""calc.exe"",""workingDir"":""%V"",""scope"":""folderBackground"",""runMode"":""background""},
                     {""id"":""e2"",""name"":""git pull"",""command"":""git pull"",""workingDir"":""%V"",""scope"":""folderBackground"",""runMode"":""visibleTerminal"",""sourceTemplateId"":""git pull"",""appliedTemplateHash"":""x"",""hidden"":true,""folderId"":""f1""}]}");
            var state = new AdditionStore(path).Load();

            Assert.Equal(5, state.SchemaVersion);
            var survivor = Assert.Single(state.Entries);
            Assert.Equal("e2", survivor.Id);
            Assert.True(survivor.Hidden);
            Assert.Equal("f1", survivor.FolderId);
            Assert.Single(state.Folders);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_migrates_v1_all_the_way_to_v5()
    {
        // v3 stamps "npm run dev" (name + structural match against the built-in
        // template) as template-derived, so v5 keeps it; "My own thing" matches
        // nothing and is dropped. Folders always survive.
        var path = TempFile();
        try
        {
            File.WriteAllText(path,
                @"{""schemaVersion"":1,
                   ""folders"":[{""id"":""f1"",""name"":""Dev tools""}],
                   ""entries"":[
                     {""id"":""e1"",""name"":""npm run dev"",""command"":""npm run dev"",""workingDir"":""%V"",""scope"":""folderBackground"",""runMode"":""visibleTerminal""},
                     {""id"":""e2"",""name"":""My own thing"",""command"":""calc.exe"",""workingDir"":""%V"",""scope"":""folderBackground"",""runMode"":""background""}]}");
            var state = new AdditionStore(path).Load();

            Assert.Equal(5, state.SchemaVersion);
            var survivor = Assert.Single(state.Entries);
            Assert.Equal("e1", survivor.Id);
            Assert.Equal("npm run dev", survivor.SourceTemplateId);
            Assert.Single(state.Folders);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
```

- [ ] **Step 3: Run the new tests, verify they fail**

Run: `dotnet test manager/RCMM.sln --filter "FullyQualifiedName~AdditionStoreTests" -v minimal`

Expected: the two new tests FAIL asserting `Assert.Equal(5, state.SchemaVersion)` (actual 4). Existing tests still pass.

- [ ] **Step 4: Bump the schema version and document v5**

In `manager/src/RCMM.Core/Models/AdditionState.cs`, extend the doc comment and bump the constant:

```csharp
///   v4 — <see cref="AdditionEntry.Hidden"/>. Absent in older documents, which
///        deserializes to false — every pre-v4 entry was visible by construction,
///        so the migration is a no-op beyond the version stamp.
///   v5 — templates-only Add page: hand-authored entries (null
///        <see cref="AdditionEntry.SourceTemplateId"/>) can no longer be created
///        or edited, so they are dropped on load. Folders always survive.
/// AdditionStore.Load migrates older schemas transparently.
/// </summary>
public sealed record AdditionState
{
    public const int CurrentSchemaVersion = 5;
```

- [ ] **Step 5: Add the v5 step to `MigrateIfNeeded`**

In `manager/src/RCMM.Core/Services/AdditionStore.cs`, insert after the v3 block (after `state = state with { Entries = migrated };` and its closing brace) and before the final `return state with { SchemaVersion = … }`:

```csharp
        // v4 → v5: templates-only Add page. Hand-authored entries can no longer be
        // created or edited, so drop them here rather than carrying dead weight the
        // UI can't manage. Must run AFTER the v3 stamping pass — v3 is what decides
        // which pre-v3 entries count as template-derived and therefore survive.
        // Registry cleanup is free: the next Apply purges every RCMM.-prefixed key
        // and rewrites from this store.
        if (state.SchemaVersion < 5)
        {
            var kept = new List<AdditionEntry>(state.Entries.Count);
            foreach (var e in state.Entries)
                if (e.SourceTemplateId != null) kept.Add(e);
            if (kept.Count != state.Entries.Count)
                Log.Info(Cat, $"v5: dropped {state.Entries.Count - kept.Count} hand-authored entries");
            state = state with { Entries = kept };
        }
```

Also update the `MigrateIfNeeded` summary comment: its "No data loss." sentence is now wrong for v5. Replace the summary with:

```csharp
    /// <summary>
    /// Migrate older schemas to the current one. v1 → v2 sets
    /// <see cref="AdditionFolder.ParentFolderId"/> = null and
    /// <see cref="AdditionFolder.Scope"/> = FolderBackground on every folder.
    /// v2 → v3 back-fills template-update tracking (see inline comment).
    /// v4 → v5 drops hand-authored entries — the one deliberately lossy step,
    /// per the templates-only design (2026-07-15 spec).
    /// </summary>
```

- [ ] **Step 6: Run the store tests, verify all pass**

Run: `dotnet test manager/RCMM.sln --filter "FullyQualifiedName~AdditionStoreTests" -v minimal`

Expected: ALL PASS, including the pre-existing `Load_migrates_v1_json_to_current_schema` (its "npm run dev" entry is stamped by v3 and survives v5) and `Save_then_Load_roundtrips_one_entry` (saved at current schema, so no migration runs).

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test manager/RCMM.sln -v minimal`

Expected: ALL PASS. If a test elsewhere hard-codes `SchemaVersion` 4 or feeds pre-v5 hand-authored fixtures through `Load()`, update that fixture to include `sourceTemplateId` (any non-null string) — but only where the test's subject is not the migration itself.

- [ ] **Step 8: Commit**

```bash
git add manager/src/RCMM.Core/Models/AdditionState.cs manager/src/RCMM.Core/Services/AdditionStore.cs manager/test/RCMM.Tests/AdditionStoreTests.cs
git commit -m "feat: schema v5 drops hand-authored entries (templates-only Add page)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Trim the Add page UI to structural editing

**Files:**
- Modify: `manager/src/RCMM/Views/AddPage.xaml` (header text ~line 31, toolbar ~lines 35-42, editor rows ~lines 332-379)
- Modify: `manager/src/RCMM/Views/AddPage.xaml.cs` (`OnNavigatedTo`, `NewEntry_Click`, `ShowEntryEditor`, `ShowFolderEditor`, `SetEntryFieldsVisibility`, `SetupTerminalRow`, `Terminal_SelectionChanged`, `RunMode_SelectionChanged`, `ReadTerminal`, `SaveCurrent`, `RecordsEffectivelyEqual`)

**Interfaces:**
- Consumes: `AddPageViewModel` unchanged API (`AddFolder`, `DeleteEntry`, `DeleteFolder`, `ReplaceEntry`, `ReplaceFolder`, `MoveEntry`, `MoveFolder`, reorders). `TemplatesPage` remains the only creator of entries.
- Produces: no new interfaces. XAML names removed: `NewEntryButton`, `CommandRow`, `CommandBox`, `WorkRunRow`, `WorkingDirBox`, `RunModeBox`, `TerminalRow`, `TerminalBox`, `TerminalCustomBox`, `ScopeRow`, `ScopeBox`, `FileTypesRow`, `FileTypesBox` — nothing outside AddPage references them (verified by grep; SettingsPage's `DefaultTerminalBox` is a different control).

- [ ] **Step 1: XAML — header copy**

In `manager/src/RCMM/Views/AddPage.xaml`, replace the heading description (the `Text=` at ~line 31):

```xml
                       Text="Add entries to the (classic) right-click menu from the template catalogue. Group them into folders that render as submenus. Drag to reorder, or drop onto a folder to move into it."/>
```

- [ ] **Step 2: XAML — toolbar**

Replace the toolbar StackPanel (lines ~35-42) so "Browse templates" leads and "+ New entry" is gone:

```xml
        <!-- Toolbar -->
        <StackPanel Grid.Row="1" Orientation="Horizontal" Spacing="10">
            <Button x:Name="TemplatesButton" Content="Browse templates" Click="Templates_Click"
                    Style="{StaticResource SurfaceButton}"/>
            <Button x:Name="NewFolderButton" Content="+ New folder" Click="NewFolder_Click"
                    Style="{StaticResource SurfaceButton}"/>
        </StackPanel>
```

- [ ] **Step 3: XAML — remove the non-structural editor rows**

Inside the `EditorPanel` StackPanel, delete these five blocks entirely (keep the Name row, `FolderRow`, `ParentFolderRow`, `IconRow`, `FolderInfoRow`, and `DeleteButton`):

1. `<StackPanel x:Name="CommandRow" …>…</StackPanel>` (Command label + `CommandBox`)
2. `<Grid x:Name="WorkRunRow" …>…</Grid>` (Working dir `WorkingDirBox` + Run mode `RunModeBox`)
3. `<StackPanel x:Name="TerminalRow" …>…</StackPanel>` (`TerminalBox` + `TerminalCustomBox`)
4. `<StackPanel x:Name="ScopeRow" …>…</StackPanel>` (`ScopeBox`)
5. `<StackPanel x:Name="FileTypesRow" …>…</StackPanel>` (`FileTypesBox`)

- [ ] **Step 4: Code-behind — remove creation and dead editor plumbing**

In `manager/src/RCMM/Views/AddPage.xaml.cs`:

a. In `OnNavigatedTo`, delete the static combo-source lines:

```csharp
        // Static combo sources
        ScopeBox.ItemsSource    = Enum.GetValues<AdditionScope>().Cast<object>().ToList();
        RunModeBox.ItemsSource  = Enum.GetValues<RunMode>().Cast<object>().ToList();
```

b. Delete the whole `NewEntry_Click` method (~lines 529-547, including its comment).

c. Delete these four members entirely: `SetupTerminalRow`, `Terminal_SelectionChanged`, `RunMode_SelectionChanged`, `ReadTerminal` (~lines 727-787).

d. Delete the `RecordsEffectivelyEqual` method (~lines 868-888). Plain record equality replaces it in `SaveCurrent` — the only reference-typed field, `FileTypes`, is carried over by `with`, so the references are identical and value equality is exact.

- [ ] **Step 5: Code-behind — slim the editor render/save paths**

a. `ShowEntryEditor`: inside the `try` block, keep only Name, folder options, and icon:

```csharp
        _suppressFieldChange = true;
        try
        {
            NameBox.Text = entry.Name;

            var folderOptions = new List<object> { TopLevelLabel };
            foreach (var f in _vm.Folders) folderOptions.Add(f);
            FolderBox.ItemsSource = folderOptions;
            FolderBox.SelectedItem = entry.FolderId == null
                ? (object)TopLevelLabel
                : _vm.Folders.FirstOrDefault(f => f.Id == entry.FolderId) ?? (object)TopLevelLabel;

            RenderIconPicker(entry.Icon);
        }
        finally { _suppressFieldChange = false; }
```

b. `ShowFolderEditor`: remove the `ScopeBox.SelectedItem = folder.Scope;` line, and shrink the visibility preamble to:

```csharp
        SetEntryFieldsVisibility(false);
        FolderInfoRow.Visibility = Visibility.Visible;
```

(`AdditionFolder.Scope` is never read by `AdditionApplier` — folder registry placement is derived from the scopes of the entries inside — so dropping its picker is behavior-neutral.)

c. `SetEntryFieldsVisibility` becomes a pure Folder/ParentFolder toggle:

```csharp
    private void SetEntryFieldsVisibility(bool showEntryFields)
    {
        FolderRow.Visibility = showEntryFields ? Visibility.Visible : Visibility.Collapsed;
        ParentFolderRow.Visibility = showEntryFields ? Visibility.Collapsed : Visibility.Visible;
    }
```

d. `SaveCurrent`: the entry branch shrinks to structural fields; the folder branch loses Scope. Full replacement method body (keep the existing leading comment about the async-event hazard):

```csharp
        if (_suppressFieldChange) return;
        if (_selectedKind == "entry" && _vm.Entries.FirstOrDefault(x => x.Id == _selectedId) is { } entry)
        {
            var newFolderId = FolderBox.SelectedItem is AdditionFolder f ? f.Id : null;
            var updated = entry with
            {
                Name = NameBox.Text,
                FolderId = newFolderId,
            };
            if (entry == updated) return;
            _vm.ReplaceEntry(updated);
            if ((entry.FolderId ?? null) != (newFolderId ?? null))
                _vm.MoveEntry(entry.Id, newFolderId);
        }
        else
        {
            var fid = CurrentMiddleFolderId() ?? _selectedId;
            if (fid != null && _vm.Folders.FirstOrDefault(f => f.Id == fid) is { } folder)
            {
                var newParent = ParentFolderBox.SelectedItem is AdditionFolder p ? p.Id : null;
                var updated = folder with { Name = NameBox.Text };
                if (folder == updated && (folder.ParentFolderId ?? null) == (newParent ?? null)) return;
                if (folder != updated) _vm.ReplaceFolder(updated);
                if ((folder.ParentFolderId ?? null) != (newParent ?? null))
                {
                    if (!_vm.MoveFolder(folder.Id, newParent))
                    {
                        Log.Warn(Cat, $"folder move refused: depth cap or cycle (id={folder.Id} new parent={newParent})");
                        ParentFolderBox.SelectedItem = folder.ParentFolderId == null
                            ? (object)TopLevelLabel
                            : _vm.Folders.FirstOrDefault(x => x.Id == folder.ParentFolderId) ?? (object)TopLevelLabel;
                    }
                }
            }
        }
```

- [ ] **Step 6: Build**

Run: `dotnet build manager/RCMM.sln`

Expected: SUCCESS, zero errors. A build error naming any removed x:Name (e.g. `CommandBox`) means a leftover reference — delete it; do not resurrect the control.

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test manager/RCMM.sln -v minimal`

Expected: ALL PASS (the viewmodel API was untouched, so `AddPageViewModelTests` are unaffected).

- [ ] **Step 8: Commit**

```bash
git add manager/src/RCMM/Views/AddPage.xaml manager/src/RCMM/Views/AddPage.xaml.cs
git commit -m "feat: trim Add page editor to templates-only structuring (name, folder, icon)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: Docs, end-to-end verification, PR

**Files:**
- Modify: `CLAUDE.md` (Scope section)
- Modify: `ROADMAP.md` (§3)

**Interfaces:**
- Consumes: Tasks 1-2 merged into the branch; issue number from Task 1 Step 1.
- Produces: the PR. Nothing downstream.

- [ ] **Step 1: Update CLAUDE.md scope**

In the **In scope, present** list, add a fourth bullet:

```markdown
- Add entries to the menu from the built-in template catalogue and structure them: drag order, folders (rendered as submenus, max 3 levels), name, and icon. Templates-only by design; there is no ad-hoc "write your own command" editor.
```

In the **In scope, planned** list, delete the **Add to RCM** bullet (it shipped, in its trimmed form) and keep **Manage New >**. Adjust the intro sentence if it now reads oddly with a single item.

- [ ] **Step 2: Update ROADMAP.md §3**

Replace the section body of `## 3. Add to RCM — broader feature` with:

```markdown
## 3. Add page — structuring polish

✅ The Add page shipped **templates-only** (2026-07, spec `docs/superpowers/specs/2026-07-15-templates-only-addpage-design.md`): entries come from the template catalogue; users structure them (drag order, folders, name, icon). The ad-hoc custom-entry editor was removed; schema v5 drops hand-authored entries on load. Consequences for the old backlog: command-quoting (AUDIT.md **H5**) is now confined to templates we author; the unused Working-dir field (AUDIT.md **M-A4**) is no longer user-editable, so the honor-or-remove question moves into template/applier territory.

- 🚧 **H6 input validation, reduced scope** — empty/duplicate *names* only (Name is still editable); command validation is moot.
- 🔭 **Full Lucide icon set** — bundle all ~1,600 Lucide icons (ISC-licensed) and rebuild the icon picker with a **search box + virtualized grid** (render-on-scroll), replacing today's hand-curated `IconLibrary` handful. Auto-generate the fragment data from the upstream SVGs (`github.com/lucide-icons/lucide` `icons/*.svg`) rather than hand-copying. A raw 1,600-icon grid is unusable without search/virtualization — that's the actual work, not the download.
```

- [ ] **Step 3: Verify the app end-to-end**

Follow the `build-release` skill's run/screenshot flow. Launch the app, then check on the Add page:

1. Toolbar shows exactly "Browse templates" and "+ New folder".
2. Browse templates → add "git pull" → it appears; selecting it shows only Name / Folder / Icon / Delete in the editor.
3. Rename it, move it into a new folder, change its icon, drag-reorder — all work; Apply commits.
4. Select a folder → editor shows Name / Parent folder / Icon / Delete (no Scope).
5. If a pre-v5 `%APPDATA%\RCMM\additions.json` with a hand-authored entry exists (create one by hand if needed), it loads without that entry and `rcmm.log` shows the `v5: dropped …` line.

Expected: all five hold. Capture a screenshot of the trimmed Add page for the PR.

- [ ] **Step 4: Commit docs**

```bash
git add CLAUDE.md ROADMAP.md
git commit -m "docs: scope + roadmap reflect templates-only Add page

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

- [ ] **Step 5: Push and open the PR**

```bash
git push -u origin feat/templates-only-addpage
gh pr create --title "Trim Add page to templates-only" --body "Closes #<issue-number-from-Task-1>.

- Schema v5: hand-authored entries (null SourceTemplateId) are dropped on load; registry keys purge on next Apply.
- Add page: '+ New entry' removed; entry editor is Name / Folder / Icon / Delete; folder editor loses its (applier-ignored) Scope picker.
- Docs: CLAUDE.md scope + ROADMAP §3 updated.

Spec: docs/superpowers/specs/2026-07-15-templates-only-addpage-design.md

🤖 Generated with [Claude Code](https://claude.com/claude-code)"
```

Expected: PR opens referencing the issue. Attach the Add page screenshot as a PR comment if captured.
