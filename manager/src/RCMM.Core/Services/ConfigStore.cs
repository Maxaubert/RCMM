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
