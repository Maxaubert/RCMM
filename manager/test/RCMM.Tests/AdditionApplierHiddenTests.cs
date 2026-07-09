using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Tests;

/// <summary>
/// Hidden-ness for RCMM-owned entries must live in AdditionState, not in the
/// registry: Apply purges every RCMM.-prefixed key and rewrites it from the
/// store, so a LegacyDisable value written directly to the key (by HideService)
/// is destroyed on the next Apply. See issue #10.
/// </summary>
public class AdditionApplierHiddenTests
{
    private const string BgShell = "Software\\Classes\\Directory\\Background\\shell";

    // ---------- LegacyDisable emission ----------

    [Fact]
    public void Apply_hidden_entry_writes_LegacyDisable()
    {
        var reg = new FakeRegistry();
        new AdditionApplier(reg).Apply(new AdditionState
        {
            Entries = new[] { Entry("abc") with { Hidden = true } }
        });

        Assert.Equal("", reg.GetValue(RegistryHive.CurrentUser, $"{BgShell}\\RCMM.001.abc", "LegacyDisable"));
    }

    [Fact]
    public void Apply_visible_entry_omits_LegacyDisable()
    {
        var reg = new FakeRegistry();
        new AdditionApplier(reg).Apply(new AdditionState
        {
            Entries = new[] { Entry("abc") }
        });

        Assert.Null(reg.GetValue(RegistryHive.CurrentUser, $"{BgShell}\\RCMM.001.abc", "LegacyDisable"));
    }

    /// <summary>The regression this whole fix exists for: a second Apply (e.g.
    /// triggered by an unrelated edit) must not silently un-hide the entry.</summary>
    [Fact]
    public void Hidden_survives_a_second_Apply()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var state = new AdditionState { Entries = new[] { Entry("abc") with { Hidden = true } } };

        applier.Apply(state);
        applier.Apply(state);

        Assert.Equal("", reg.GetValue(RegistryHive.CurrentUser, $"{BgShell}\\RCMM.001.abc", "LegacyDisable"));
    }

    [Fact]
    public void Unhiding_clears_LegacyDisable()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var path = $"{BgShell}\\RCMM.001.abc";

        applier.Apply(new AdditionState { Entries = new[] { Entry("abc") with { Hidden = true } } });
        Assert.Equal("", reg.GetValue(RegistryHive.CurrentUser, path, "LegacyDisable"));

        applier.Apply(new AdditionState { Entries = new[] { Entry("abc") with { Hidden = false } } });
        Assert.Null(reg.GetValue(RegistryHive.CurrentUser, path, "LegacyDisable"));
    }

    [Fact]
    public void Hidden_entry_inside_a_folder_writes_LegacyDisable_on_the_submenu_verb()
    {
        var reg = new FakeRegistry();
        new AdditionApplier(reg).Apply(new AdditionState
        {
            Folders = new[] { new AdditionFolder { Id = "f1", Name = "Tools" } },
            Entries = new[] { Entry("abc") with { Hidden = true, FolderId = "f1" } },
        });

        var path = "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.001.f1\\shell\\RCMM.001.abc";
        Assert.Equal("", reg.GetValue(RegistryHive.CurrentUser, path, "LegacyDisable"));
    }

    // ---------- Owned-verb parsing ----------

    [Theory]
    [InlineData("RCMM.001.abc", "abc")]
    [InlineData("RCMM.004.d50387a34cb7496d93116233eb64e326", "d50387a34cb7496d93116233eb64e326")]
    [InlineData("RCMM.123.x", "x")]
    public void TryParseOwnedVerb_extracts_the_entry_id(string keyName, string expectedId)
    {
        Assert.True(AdditionApplier.TryParseOwnedVerb(keyName, out var id));
        Assert.Equal(expectedId, id);
    }

    [Theory]
    [InlineData("Tabby")]              // a foreign verb, e.g. Tabby's own integration
    [InlineData("OpenInTerminal")]
    [InlineData("RCMM.abc")]           // no ordinal segment
    [InlineData("RCMM.01.abc")]        // ordinal not 3 digits
    [InlineData("RCMM.001.")]          // empty id
    [InlineData("RCMM.")]
    [InlineData("")]
    public void TryParseOwnedVerb_rejects_non_owned_names(string keyName)
    {
        Assert.False(AdditionApplier.TryParseOwnedVerb(keyName, out _));
    }

    private static AdditionEntry Entry(string id) => new()
    {
        Id = id, Name = id, Command = "npm run dev", WorkingDir = "%V",
        Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
    };
}
