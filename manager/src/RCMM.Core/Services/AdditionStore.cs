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
