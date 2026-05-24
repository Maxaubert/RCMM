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
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), _opts) ?? new AppSettings();
        }
        catch (Exception ex) { Log.Error(Cat, $"load failed ({_path}): {ex.Message}"); }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, _opts));
            Log.Info(Cat, $"saved (restartExplorerOnApply={settings.RestartExplorerOnApply})");
        }
        catch (Exception ex) { Log.Error(Cat, $"save failed ({_path}): {ex.Message}"); }
    }
}
