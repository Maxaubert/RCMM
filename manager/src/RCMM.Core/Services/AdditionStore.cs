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
            state = MigrateIfNeeded(state);
            Log.Info(Cat, $"loaded entries={state.Entries.Count} folders={state.Folders.Count} schema=v{state.SchemaVersion}");
            return state;
        }
        catch (Exception ex)
        {
            Log.Error(Cat, $"failed to read {_path} — returning empty state", ex);
            return new AdditionState();
        }
    }

    /// <summary>
    /// Migrate older schemas to the current one. v1 → v2 sets
    /// <see cref="AdditionFolder.ParentFolderId"/> = null and
    /// <see cref="AdditionFolder.Scope"/> = FolderBackground on every folder
    /// (both fields ship as defaults from the record, but we bump the version
    /// explicitly so a subsequent save records v2 in the file). No data loss.
    /// </summary>
    public static AdditionState MigrateIfNeeded(AdditionState state)
    {
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
                if (e.SourceTemplateId != null || !byName.TryGetValue(e.Name, out var t))
                {
                    migrated.Add(e);   // already stamped, or not from a known template (renamed/hand-authored)
                }
                else
                {
                    // Baseline = the live template hash only when the entry's non-command
                    // fields still match it (in sync). If they've drifted — e.g. an old
                    // Change format entry missing heic/jxl — leave the baseline null so it
                    // surfaces as an available update. (Command is excluded from the match:
                    // the entry's is path-expanded, the template's still has placeholders.)
                    var inSync = TemplateUpdateService.MatchesIgnoringCommand(e, t);
                    migrated.Add(e with
                    {
                        SourceTemplateId = t.Name,
                        AppliedTemplateHash = inSync ? TemplateUpdateService.Hash(t) : null,
                    });
                }
            }
            state = state with { Entries = migrated };
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
