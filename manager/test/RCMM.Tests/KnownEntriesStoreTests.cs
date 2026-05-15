using System.IO;
using System.Linq;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Tests;

public class KnownEntriesStoreTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"rcmm-known-{System.Guid.NewGuid():N}.json");

    [Fact]
    public void Load_returns_empty_when_file_missing()
    {
        var store = new KnownEntriesStore(TempFile());
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Save_then_Load_roundtrips_a_verb_entry_with_hide_targets()
    {
        var path = TempFile();
        try
        {
            var store = new KnownEntriesStore(path);
            var entry = new MenuEntry
            {
                Id = "verb:windows.share",
                DisplayName = "Give access to",
                Source = "Microsoft Corporation",
                IsBuiltIn = true,
                IsSubmenu = true,
                IsHidden = false,
                HideTargets = new[]
                {
                    new HideTarget(HideKind.BlockedShellExt, RegistryHive.CurrentUser,
                        "Software\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Blocked",
                        "{e2bf9676-5f8f-435c-97eb-11607a5bedf7}"),
                },
            };
            store.Save(new[] { entry });

            var reloaded = store.Load();
            Assert.Single(reloaded);
            var r = reloaded[0];
            Assert.Equal("verb:windows.share", r.Id);
            Assert.Equal("Give access to", r.DisplayName);
            Assert.True(r.IsSubmenu);
            Assert.Single(r.HideTargets);
            Assert.Equal(HideKind.BlockedShellExt, r.HideTargets[0].Kind);
            Assert.Equal("{e2bf9676-5f8f-435c-97eb-11607a5bedf7}", r.HideTargets[0].ValueName);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Save_strips_icon_bytes_to_keep_file_small()
    {
        var path = TempFile();
        try
        {
            var store = new KnownEntriesStore(path);
            var entry = new MenuEntry
            {
                Id = "x",
                DisplayName = "x",
                IconBytes = new byte[] { 1, 2, 3, 4, 5 },
                IconPath = "C:\\test.dll",
                HideTargets = System.Array.Empty<HideTarget>(),
            };
            store.Save(new[] { entry });
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("iconBytes", json);
            // Reload — should have null IconBytes
            var reloaded = store.Load();
            Assert.Null(reloaded[0].IconBytes);
            Assert.Equal("C:\\test.dll", reloaded[0].IconPath);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_with_corrupt_json_returns_empty()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{not valid json");
            var store = new KnownEntriesStore(path);
            Assert.Empty(store.Load());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
