using System;
using System.IO;
using System.Text.Json;
using RCMM.Core.Diagnostics;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

/// <summary>Reads/writes the user's app preferences at %APPDATA%\RCMM\settings.json.
/// Missing or unreadable file yields defaults, so the app always has valid settings.</summary>
public sealed class SettingsStore
{
    private const string Cat = "settings";
    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;

    public SettingsStore(string path) { _path = path; }
    public SettingsStore() : this(DefaultPath()) { }

    public static string DefaultPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RCMM", "settings.json");

    public AppSettings Load()
    {
        if (!File.Exists(_path)) return new AppSettings();
        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), _opts);
            if (loaded != null) return loaded;
            Log.Warn(Cat, $"deserialize returned null ({_path})");
        }
        catch (Exception ex) { Log.Error(Cat, $"load failed ({_path}): {ex.Message}"); }
        // Unreadable ≠ absent: keep a copy before the next Save overwrites the
        // file, so a corrupt-or-locked settings.json can still be hand-recovered.
        try { File.Copy(_path, _path + ".bad", overwrite: true); Log.Warn(Cat, $"backed up unreadable file to {_path}.bad"); }
        catch (Exception ex) { Log.Error(Cat, $"backup of unreadable file failed: {ex.Message}"); }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Atomic write-tmp-then-rename, same as AdditionStore/KnownEntriesStore:
            // a crash mid-write must not leave settings.json truncated (Load would
            // then silently revert RestartExplorerOnApply / DefaultTerminal to
            // defaults with no signal to the user).
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, _opts));
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
            Log.Info(Cat, $"saved (restartExplorerOnApply={settings.RestartExplorerOnApply})");
        }
        catch (Exception ex) { Log.Error(Cat, $"save failed ({_path}): {ex.Message}"); }
    }
}
