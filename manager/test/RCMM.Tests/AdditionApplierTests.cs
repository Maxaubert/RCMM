using System.Linq;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Tests;

public class AdditionApplierTests
{
    // ---------- Scope path mapping ----------

    [Theory]
    [InlineData(AdditionScope.FolderBackground,     "Directory\\Background")]
    [InlineData(AdditionScope.Folder,               "Directory")]
    [InlineData(AdditionScope.Drive,                "Drive")]
    [InlineData(AdditionScope.AllFilesystemObjects, "AllFilesystemObjects")]
    [InlineData(AdditionScope.File,                 "*")]
    public void ScopeRootFor_returns_correct_root(AdditionScope scope, string expected)
    {
        Assert.Equal(expected, AdditionApplier.ScopeRootFor(scope));
    }

    // ---------- Command wrapping ----------

    [Fact]
    public void WrapForRunMode_VisibleTerminal_wraps_in_cmd_k()
        => Assert.Equal("cmd /k npm run dev", AdditionApplier.WrapForRunMode(RunMode.VisibleTerminal, "npm run dev"));

    [Fact]
    public void WrapForRunMode_Background_returns_bare_command()
        => Assert.Equal("foo", AdditionApplier.WrapForRunMode(RunMode.Background, "foo"));

    // ---------- Apply: single entry ----------

    [Fact]
    public void Apply_top_level_entry_uses_ordinal_001_in_verb_name()
    {
        var reg = new FakeRegistry();
        new AdditionApplier(reg).Apply(new AdditionState
        {
            Entries = new[] { Entry("abc", "npm run dev", AdditionScope.FolderBackground) }
        });

        // Ordinal prefix forces Windows to render verbs in user order, so the verb
        // name is RCMM.<3-digit-ord>.<id> not RCMM.<id>.
        var verbPath = "Software\\Classes\\Directory\\Background\\shell\\RCMM.001.abc";
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, verbPath));
        Assert.Equal("npm run dev", reg.GetValue(RegistryHive.CurrentUser, verbPath, ""));
        Assert.Equal("cmd /k npm run dev", reg.GetValue(RegistryHive.CurrentUser, verbPath + "\\command", ""));
    }

    [Fact]
    public void Apply_writes_Icon_when_set_omits_it_when_unset()
    {
        var reg = new FakeRegistry();
        new AdditionApplier(reg).Apply(new AdditionState
        {
            Entries = new[]
            {
                Entry("withicon", "x", AdditionScope.FolderBackground) with { Icon = "shell32.dll,42" },
                Entry("noicon",   "x", AdditionScope.FolderBackground),
            }
        });
        Assert.Equal("shell32.dll,42",
            reg.GetValue(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\shell\\RCMM.001.withicon", "Icon"));
        Assert.Null(
            reg.GetValue(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\shell\\RCMM.002.noicon", "Icon"));
    }

    [Fact]
    public void Apply_File_scope_with_multiple_extensions_writes_one_per_ext()
    {
        var reg = new FakeRegistry();
        var entry = Entry("img", "fileinfo %1", AdditionScope.File) with { FileTypes = new[] { ".png", ".jpg" } };
        new AdditionApplier(reg).Apply(new AdditionState { Entries = new[] { entry } });
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\.png\\shell\\RCMM.001.img"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\.jpg\\shell\\RCMM.001.img"));
    }

    [Fact]
    public void Apply_File_scope_without_extensions_uses_wildcard()
    {
        var reg = new FakeRegistry();
        new AdditionApplier(reg).Apply(new AdditionState
        {
            Entries = new[] { Entry("x", "noop", AdditionScope.File) with { FileTypes = null } }
        });
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\*\\shell\\RCMM.001.x"));
    }

    // ---------- Apply: ordinal prefixes preserve insertion order ----------

    [Fact]
    public void Apply_assigns_ordinals_001_002_003_in_list_order()
    {
        var reg = new FakeRegistry();
        new AdditionApplier(reg).Apply(new AdditionState
        {
            Entries = new[]
            {
                Entry("first",  "a", AdditionScope.FolderBackground),
                Entry("second", "b", AdditionScope.FolderBackground),
                Entry("third",  "c", AdditionScope.FolderBackground),
            }
        });
        var keys = reg.GetSubKeyNames(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\shell").ToList();
        Assert.Contains("RCMM.001.first",  keys);
        Assert.Contains("RCMM.002.second", keys);
        Assert.Contains("RCMM.003.third",  keys);
    }

    // ---------- Apply: single-level folder ----------

    [Fact]
    public void Apply_folder_with_two_children_builds_submenu_tree_with_ordinals()
    {
        var reg = new FakeRegistry();
        var folder = new AdditionFolder { Id = "folder1", Name = "Dev tools", Scope = AdditionScope.FolderBackground };
        new AdditionApplier(reg).Apply(new AdditionState
        {
            Folders = new[] { folder },
            Entries = new[]
            {
                Entry("c1", "npm run dev", AdditionScope.FolderBackground) with { FolderId = "folder1" },
                Entry("c2", "git pull",    AdditionScope.FolderBackground) with { FolderId = "folder1" },
            }
        });

        // Top-level folder verb is the only thing at the bucket (no top-level entries),
        // so it gets RCMM.001.folder1.
        var parentPath = "Software\\Classes\\Directory\\Background\\shell\\RCMM.001.folder1";
        Assert.Equal("Dev tools", reg.GetValue(RegistryHive.CurrentUser, parentPath, ""));
        Assert.Equal("Directory\\Background\\ContextMenus\\RCMM.001.folder1",
            reg.GetValue(RegistryHive.CurrentUser, parentPath, "ExtendedSubCommandsKey"));

        Assert.Equal("npm run dev",
            reg.GetValue(RegistryHive.CurrentUser,
                "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.001.folder1\\shell\\RCMM.001.c1", ""));
        Assert.Equal("cmd /k git pull",
            reg.GetValue(RegistryHive.CurrentUser,
                "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.001.folder1\\shell\\RCMM.002.c2\\command", ""));
    }

    [Fact]
    public void Apply_folder_with_children_in_two_scopes_registers_under_both()
    {
        var reg = new FakeRegistry();
        var folder = new AdditionFolder { Id = "f", Name = "Mixed" };
        new AdditionApplier(reg).Apply(new AdditionState
        {
            Folders = new[] { folder },
            Entries = new[]
            {
                Entry("a", "echo bg",     AdditionScope.FolderBackground) with { FolderId = "f" },
                Entry("b", "echo folder", AdditionScope.Folder)           with { FolderId = "f" },
            }
        });

        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\shell\\RCMM.001.f"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\shell\\RCMM.001.f"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.001.f\\shell\\RCMM.001.a"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\ContextMenus\\RCMM.001.f\\shell\\RCMM.001.b"));
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.001.f\\shell\\RCMM.001.b"),
            "child b only belongs to its own scope's ContextMenus");
    }

    // ---------- Apply: nested folders (schema v2) ----------

    [Fact]
    public void Apply_nested_folder_builds_recursive_submenu_chain()
    {
        var reg = new FakeRegistry();
        var outer = new AdditionFolder { Id = "outer", Name = "Outer" };
        var inner = new AdditionFolder { Id = "inner", Name = "Inner", ParentFolderId = "outer" };
        new AdditionApplier(reg).Apply(new AdditionState
        {
            Folders = new[] { outer, inner },
            Entries = new[] { Entry("leaf", "x", AdditionScope.FolderBackground) with { FolderId = "inner" } }
        });

        // outer folder verb at top level
        var outerVerb = "Software\\Classes\\Directory\\Background\\shell\\RCMM.001.outer";
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, outerVerb));
        Assert.Equal("Directory\\Background\\ContextMenus\\RCMM.001.outer",
            reg.GetValue(RegistryHive.CurrentUser, outerVerb, "ExtendedSubCommandsKey"));

        // inner folder verb lives inside outer's ContextMenus subtree
        var innerVerb = "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.001.outer\\shell\\RCMM.001.inner";
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, innerVerb));
        // …and points to a deeper ContextMenus path under outer's CM
        Assert.Equal("Directory\\Background\\ContextMenus\\RCMM.001.outer\\ContextMenus\\RCMM.001.inner",
            reg.GetValue(RegistryHive.CurrentUser, innerVerb, "ExtendedSubCommandsKey"));

        // leaf entry sits inside inner's CM
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.001.outer\\ContextMenus\\RCMM.001.inner\\shell\\RCMM.001.leaf"));
    }

    [Fact]
    public void Apply_skips_empty_folder_in_a_scope_it_doesnt_participate_in()
    {
        // A folder whose only entries are File-scope should not appear under Folder Background.
        var reg = new FakeRegistry();
        var f = new AdditionFolder { Id = "f", Name = "Files only" };
        new AdditionApplier(reg).Apply(new AdditionState
        {
            Folders = new[] { f },
            Entries = new[]
            {
                Entry("a", "fileinfo %1", AdditionScope.File) with { FolderId = "f", FileTypes = new[] { ".png" } }
            }
        });
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\shell\\RCMM.001.f"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\.png\\shell\\RCMM.001.f"));
    }

    // ---------- Purge ----------

    [Fact]
    public void Purge_removes_all_RCMM_prefixed_keys_under_known_roots()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\shell\\RCMM.old1", "", "Old 1");
        reg.SetValue(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\shell\\RCMM.001.old2\\command", "", "x");
        reg.SetValue(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\shell\\NotOurs", "", "leave alone");
        reg.SetValue(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.001.oldfolder\\shell\\RCMM.001.kid", "", "x");
        reg.SetValue(RegistryHive.CurrentUser, "Software\\Classes\\.png\\shell\\RCMM.001.imgverb", "", "x");

        new AdditionApplier(reg).PurgeOwnedKeys(new[] { ".png" });

        Assert.False(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\shell\\RCMM.old1"));
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\shell\\RCMM.001.old2"));
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.001.oldfolder"));
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\.png\\shell\\RCMM.001.imgverb"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\shell\\NotOurs"));
    }

    // ---------- Apply: idempotence ----------

    [Fact]
    public void Apply_is_idempotent_running_twice_produces_same_state()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var state = new AdditionState
        {
            Entries = new[] { Entry("x", "x", AdditionScope.FolderBackground) }
        };
        applier.Apply(state);
        var afterFirst = reg.GetSubKeyNames(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\shell").ToList();
        applier.Apply(state);
        var afterSecond = reg.GetSubKeyNames(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\shell").ToList();
        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public void Apply_with_empty_state_purges_previous_owned_keys()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        applier.Apply(new AdditionState
        {
            Entries = new[] { Entry("leftover", "x", AdditionScope.FolderBackground) }
        });
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\shell\\RCMM.001.leftover"));
        applier.Apply(new AdditionState());
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\Directory\\Background\\shell\\RCMM.001.leftover"));
    }

    // ---------- helper ----------
    private static AdditionEntry Entry(string id, string command, AdditionScope scope)
        => new AdditionEntry
        {
            Id = id, Name = command, Command = command, WorkingDir = "%V",
            Scope = scope, RunMode = RunMode.VisibleTerminal
        };
}
