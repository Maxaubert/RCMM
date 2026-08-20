using System;
using System.Collections.Generic;
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

    public AdditionState Load() => Load(out _, out _);

    /// <summary>See <see cref="Load(out bool, out bool)"/>.</summary>
    public AdditionState Load(out bool droppedHandAuthored) => Load(out droppedHandAuthored, out _);

    /// <summary>
    /// Same as <see cref="Load()"/>, plus two out-flags.
    /// <paramref name="droppedHandAuthored"/>: true iff the v4 → v5 migration dropped
    /// one or more hand-authored entries from this file. Callers use this to re-arm
    /// HasPendingChanges so the drop's registry cleanup (purge + rewrite on the next
    /// Apply) is actually reachable — see AddPageViewModel.Load.
    /// <paramref name="loadFailed"/>: true when the file EXISTS but could not be read
    /// or parsed. Absent-vs-unreadable matters: a transient AV/backup/OneDrive lock is
    /// indistinguishable from corruption here, and the returned empty state must never
    /// be applied or saved over the user's real data — Apply refuses while this flag
    /// is up (see MainViewModel.ApplyPendingCore), and the unreadable file is parked
    /// at additions.json.bad so nothing is lost either way.
    /// </summary>
    public AdditionState Load(out bool droppedHandAuthored, out bool loadFailed)
    {
        droppedHandAuthored = false;
        loadFailed = false;
        if (!File.Exists(_path))
        {
            Log.Info(Cat, $"file missing — returning empty state ({_path})");
            return new AdditionState { SchemaVersion = AdditionState.CurrentSchemaVersion };
        }
        try
        {
            var json = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize<AdditionState>(json, _options);
            if (state == null)
            {
                Log.Warn(Cat, "deserialize returned null — returning empty state");
                BackupUnreadable();
                loadFailed = true;
                return new AdditionState { SchemaVersion = AdditionState.CurrentSchemaVersion };
            }
            state = MigrateIfNeeded(state, out droppedHandAuthored);
            Log.Info(Cat, $"loaded entries={state.Entries.Count} folders={state.Folders.Count} schema=v{state.SchemaVersion}");
            return state;
        }
        catch (Exception ex)
        {
            Log.Error(Cat, $"failed to read {_path} — returning empty state", ex);
            BackupUnreadable();
            loadFailed = true;
            return new AdditionState { SchemaVersion = AdditionState.CurrentSchemaVersion };
        }
    }

    // Park a copy of the unreadable file BEFORE anyone can overwrite it. Without
    // this, the sequence "transient read failure → user edits the (empty-looking)
    // Add page → Apply purges + rewrites from near-empty state → Save overwrites
    // additions.json" permanently destroys every previously added entry even
    // though the file was never actually corrupt.
    private void BackupUnreadable()
    {
        try { File.Copy(_path, _path + ".bad", overwrite: true); Log.Warn(Cat, $"backed up unreadable file to {_path}.bad"); }
        catch (Exception ex) { Log.Error(Cat, "backup of unreadable file failed", ex); }
    }

    /// <summary>
    /// Migrate older schemas to the current one. v1 → v2 sets
    /// <see cref="AdditionFolder.ParentFolderId"/> = null and
    /// <see cref="AdditionFolder.Scope"/> = FolderBackground on every folder.
    /// v2 → v3 back-fills template-update tracking (see inline comment).
    /// v4 → v5 drops hand-authored entries — the one deliberately lossy step,
    /// per the templates-only design (2026-07-15 spec).
    /// v5 → v6 re-keys SourceTemplateId from template Name to stable slug Id.
    /// </summary>
    public static AdditionState MigrateIfNeeded(AdditionState state) => MigrateIfNeeded(state, out _);

    /// <summary>
    /// Same as <see cref="MigrateIfNeeded(AdditionState)"/>, plus <paramref name="droppedHandAuthored"/>:
    /// true iff the v4 → v5 step below dropped one or more hand-authored entries.
    /// </summary>
    public static AdditionState MigrateIfNeeded(AdditionState state, out bool droppedHandAuthored)
    {
        droppedHandAuthored = false;
        if (state.SchemaVersion >= AdditionState.CurrentSchemaVersion) return state;
        Log.Info(Cat, $"migrating schema v{state.SchemaVersion} → v{AdditionState.CurrentSchemaVersion}");

        // v2 → v3: back-fill template-update tracking. Entries created before this
        // feature carry no SourceTemplateId, so we best-effort match each entry to a
        // built-in template by Name. The baseline hash is the ENTRY's current fields,
        // so an entry that already drifted from its (now newer) template — e.g. a
        // Change format added before the heic/jxl expansion — surfaces immediately as
        // an available update, while one that still matches stays quiet.
        if (state.SchemaVersion < 3)
        {
            var byName = new Dictionary<string, AdditionTemplates.Template>(StringComparer.Ordinal);
            foreach (var t in AdditionTemplates.All) byName[t.Name] = t;

            var migrated = new List<AdditionEntry>(state.Entries.Count);
            foreach (var e in state.Entries)
            {
                // Only stamp an entry as template-derived when its name matches a built-in
                // AND its structural fields (scope / run mode / working dir / file types)
                // match too. A name-only collision is treated as hand-authored: we cannot
                // tell a genuinely-drifted legacy template entry from a user who simply
                // named their own entry "Command Prompt here", and stamping the latter
                // would later offer a template "update" that overwrites their command.
                // Cost of the safe choice: a real pre-v3 entry that drifted from its
                // template won't get an update offer; the user can re-add it. See the
                // migration-by-name audit finding.
                if (e.SourceTemplateId != null
                    || !byName.TryGetValue(e.Name, out var t)
                    || !TemplateUpdateService.MatchesIgnoringCommand(e, t))
                {
                    migrated.Add(e);
                }
                else
                {
                    migrated.Add(e with
                    {
                        SourceTemplateId = t.Id,   // stable slug, not the renameable Name (v6 scheme)
                        AppliedTemplateHash = TemplateUpdateService.Hash(t),
                    });
                }
            }
            state = state with { Entries = migrated };
        }

        // v4 → v5: templates-only Add page. Hand-authored entries can no longer be
        // created or edited, so drop them here rather than carrying dead weight the
        // UI can't manage. Must run AFTER the v3 stamping pass — v3 is what decides
        // which pre-v3 entries count as template-derived and therefore survive.
        // Registry cleanup happens on the next Apply: all roots — including
        // per-extension ones, which PurgeOwnedKeys discovers by enumerating HKCU's
        // own extension keys (#23) — are purged and rewritten from the surviving
        // state (droppedHandAuthored below is what makes that Apply reachable when
        // everything was dropped — see AddPageViewModel.Load).
        if (state.SchemaVersion < 5)
        {
            var kept = new List<AdditionEntry>(state.Entries.Count);
            foreach (var e in state.Entries)
                if (e.SourceTemplateId != null) kept.Add(e);
            if (kept.Count != state.Entries.Count)
            {
                Log.Info(Cat, $"v5: dropped {state.Entries.Count - kept.Count} hand-authored entries");
                droppedHandAuthored = true;
            }
            state = state with { Entries = kept };
        }

        // v5 → v6: SourceTemplateId moves from the template's display Name to its
        // stable slug Id, so a future catalogue rename can't sever update tracking.
        // Match each tracked entry's stored value against current Names once, here.
        // Entries stamped by the v3 pass above already carry an Id (no Name will
        // match them); a value matching neither means the template was removed —
        // leave it, TemplateFor already returns null for unknown ids.
        if (state.SchemaVersion < 6)
        {
            var idByName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in AdditionTemplates.All) idByName[t.Name] = t.Id;

            var rekeyed = new List<AdditionEntry>(state.Entries.Count);
            foreach (var e in state.Entries)
            {
                if (e.SourceTemplateId != null && idByName.TryGetValue(e.SourceTemplateId, out var id))
                    rekeyed.Add(e with { SourceTemplateId = id });
                else
                    rekeyed.Add(e);
            }
            state = state with { Entries = rekeyed };
        }

        return state with { SchemaVersion = AdditionState.CurrentSchemaVersion };
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
