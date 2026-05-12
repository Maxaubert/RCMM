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
