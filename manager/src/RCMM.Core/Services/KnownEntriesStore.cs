using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RCMM.Core.Diagnostics;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

/// <summary>
/// Persists the set of menu entries RCMM has observed at least once. The
/// rescan pipeline writes this snapshot at the end of every successful pass
/// and reads it at the start of the next pass to recover "ghost" entries —
/// rows that disappeared from live capture because the user hid them, but
/// have no registry presence the classic scanners can find.
///
/// CommandStore-only verbs (Windows.Share / "Give access to", and friends)
/// are the canonical case: their hide path is BlockedShellExt on handler
/// CLSIDs, the verb name itself doesn't live under HKCR\&lt;scope&gt;\shell, and
/// hiding them makes the live IContextMenu probe stop returning them.
/// Without this store the user can't see them in RCMM after hide, and so
/// can't un-toggle.
/// </summary>
public sealed class KnownEntriesStore
{
    private const string Cat = "knownstore";
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        }
    };

    private readonly string _path;

    public KnownEntriesStore(string path) { _path = path; }

    /// <summary>Default location: %LOCALAPPDATA%\RCMM\known-entries.json.</summary>
    public static string DefaultPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RCMM", "known-entries.json");

    public IReadOnlyList<MenuEntry> Load()
    {
        if (!File.Exists(_path))
        {
            Log.Info(Cat, $"file missing — returning empty list ({_path})");
            return Array.Empty<MenuEntry>();
        }
        try
        {
            var json = File.ReadAllText(_path);
            var entries = JsonSerializer.Deserialize<List<MenuEntry>>(json, _options);
            if (entries == null)
            {
                Log.Warn(Cat, "deserialize returned null — empty list");
                return Array.Empty<MenuEntry>();
            }
            Log.Info(Cat, $"loaded knownEntries={entries.Count}");
            return entries;
        }
        catch (Exception ex)
        {
            Log.Error(Cat, $"failed to read {_path} — returning empty list", ex);
            return Array.Empty<MenuEntry>();
        }
    }

    public void Save(IEnumerable<MenuEntry> entries)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            var list = new List<MenuEntry>(entries);
            // Don't persist icon bytes — they can be megabytes total and the
            // icon loader fetches them fresh from IconPath each rescan anyway.
            var stripped = list.ConvertAll(e => e with { IconBytes = null });
            var json = JsonSerializer.Serialize(stripped, _options);
            File.WriteAllText(tmp, json);
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
            Log.Info(Cat, $"saved knownEntries={list.Count}");
        }
        catch (Exception ex)
        {
            Log.Error(Cat, "save failed", ex);
        }
    }
}
