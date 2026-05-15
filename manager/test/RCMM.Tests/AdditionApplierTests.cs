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
    {
        var result = AdditionApplier.WrapForRunMode(RunMode.VisibleTerminal, "npm run dev");
        Assert.Equal("cmd /k npm run dev", result);
    }

    [Fact]
    public void WrapForRunMode_Background_returns_bare_command()
    {
        var result = AdditionApplier.WrapForRunMode(RunMode.Background, "start \"\" \"C:\\path\\app.exe\"");
        Assert.Equal("start \"\" \"C:\\path\\app.exe\"", result);
    }

    // ---------- WriteEntry ----------

    [Fact]
    public void WriteEntry_top_level_FolderBackground_creates_expected_keys()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var entry = new AdditionEntry
        {
            Id = "abc",
            Name = "npm run dev",
            Command = "npm run dev",
            WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground,
            RunMode = RunMode.VisibleTerminal,
        };
        applier.WriteEntry(entry, parentContainer: null);
        var verbPath = "Software\\Classes\\Directory\\Background\\shell\\RCMM.abc";
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, verbPath));
        Assert.Equal("npm run dev", reg.GetValue(RegistryHive.CurrentUser, verbPath, ""));
        Assert.Equal("cmd /k npm run dev", reg.GetValue(RegistryHive.CurrentUser, verbPath + "\\command", ""));
    }

    [Fact]
    public void WriteEntry_with_icon_writes_Icon_value()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var entry = new AdditionEntry
        {
            Id = "abc",
            Name = "Test",
            Icon = "C:\\Windows\\System32\\shell32.dll,42",
            Command = "echo hi",
            WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground,
            RunMode = RunMode.VisibleTerminal,
        };
        applier.WriteEntry(entry, parentContainer: null);
        Assert.Equal("C:\\Windows\\System32\\shell32.dll,42",
            reg.GetValue(RegistryHive.CurrentUser,
                "Software\\Classes\\Directory\\Background\\shell\\RCMM.abc", "Icon"));
    }

    [Fact]
    public void WriteEntry_without_icon_does_not_write_Icon_value()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var entry = new AdditionEntry
        {
            Id = "abc", Name = "Test", Command = "x", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
        };
        applier.WriteEntry(entry, parentContainer: null);
        Assert.Null(reg.GetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.abc", "Icon"));
    }

    [Fact]
    public void WriteEntry_File_scope_with_multiple_extensions_writes_one_per_ext()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var entry = new AdditionEntry
        {
            Id = "img", Name = "Hash this image", Command = "fileinfo %1",
            WorkingDir = "%V",
            Scope = AdditionScope.File,
            FileTypes = new[] { ".png", ".jpg" },
            RunMode = RunMode.VisibleTerminal,
        };
        applier.WriteEntry(entry, parentContainer: null);
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\.png\\shell\\RCMM.img"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\.jpg\\shell\\RCMM.img"));
    }

    [Fact]
    public void WriteEntry_File_scope_without_extensions_uses_wildcard()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var entry = new AdditionEntry
        {
            Id = "x", Name = "Any file", Command = "noop",
            WorkingDir = "%V",
            Scope = AdditionScope.File,
            FileTypes = null,
            RunMode = RunMode.VisibleTerminal,
        };
        applier.WriteEntry(entry, parentContainer: null);
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser, "Software\\Classes\\*\\shell\\RCMM.x"));
    }

    // ---------- WriteFolder ----------

    [Fact]
    public void WriteFolder_with_two_children_creates_parent_and_submenu_tree()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var folder = new AdditionFolder { Id = "folder1", Name = "Dev tools" };
        var child1 = new AdditionEntry
        {
            Id = "c1", Name = "npm run dev", Command = "npm run dev", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
            FolderId = "folder1",
        };
        var child2 = new AdditionEntry
        {
            Id = "c2", Name = "git pull", Command = "git pull", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
            FolderId = "folder1",
        };
        applier.WriteFolder(folder, new[] { child1, child2 });

        var parentPath = "Software\\Classes\\Directory\\Background\\shell\\RCMM.folder1";
        Assert.Equal("Dev tools", reg.GetValue(RegistryHive.CurrentUser, parentPath, ""));
        Assert.Equal("Directory\\Background\\ContextMenus\\RCMM.folder1",
            reg.GetValue(RegistryHive.CurrentUser, parentPath, "ExtendedSubCommandsKey"));

        Assert.Equal("npm run dev", reg.GetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.folder1\\shell\\RCMM.c1", ""));
        Assert.Equal("cmd /k git pull", reg.GetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.folder1\\shell\\RCMM.c2\\command", ""));
    }

    [Fact]
    public void WriteFolder_with_children_in_two_scopes_registers_parent_under_both()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var folder = new AdditionFolder { Id = "f", Name = "Mixed" };
        var bg = new AdditionEntry
        {
            Id = "a", Name = "BG", Command = "echo bg", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal, FolderId = "f",
        };
        var folderScope = new AdditionEntry
        {
            Id = "b", Name = "Folder", Command = "echo folder", WorkingDir = "%V",
            Scope = AdditionScope.Folder, RunMode = RunMode.VisibleTerminal, FolderId = "f",
        };
        applier.WriteFolder(folder, new[] { bg, folderScope });

        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.f"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\shell\\RCMM.f"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.f\\shell\\RCMM.a"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\ContextMenus\\RCMM.f\\shell\\RCMM.b"));
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.f\\shell\\RCMM.b"),
            "child b should only appear in its own scope's ContextMenus");
    }

    // ---------- Purge ----------

    [Fact]
    public void Purge_removes_all_RCMM_prefixed_keys_under_known_roots()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.old1", "", "Old 1");
        reg.SetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.old2\\command", "", "x");
        reg.SetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\NotOurs", "", "leave alone");
        reg.SetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.oldfolder\\shell\\RCMM.kid", "", "x");
        reg.SetValue(RegistryHive.CurrentUser,
            "Software\\Classes\\.png\\shell\\RCMM.imgverb", "", "x");

        var applier = new AdditionApplier(reg);
        applier.PurgeOwnedKeys(new[] { ".png" });

        Assert.False(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.old1"));
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.old2"));
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.oldfolder"));
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\.png\\shell\\RCMM.imgverb"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\NotOurs"),
            "non-RCMM-prefixed key should be left alone");
    }

    // ---------- Apply ----------

    [Fact]
    public void Apply_writes_top_level_entries_and_folder_entries()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var folder = new AdditionFolder { Id = "f", Name = "Dev" };
        var top = new AdditionEntry
        {
            Id = "top", Name = "Open Notes", Command = "notepad", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
        };
        var nested = new AdditionEntry
        {
            Id = "nested", Name = "npm run dev", Command = "npm run dev", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
            FolderId = "f",
        };
        applier.Apply(new AdditionState
        {
            Folders = new[] { folder },
            Entries = new[] { top, nested },
        });

        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.top"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.f"));
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.f\\shell\\RCMM.nested"));
    }

    [Fact]
    public void Apply_is_idempotent_running_twice_produces_same_state()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        var state = new AdditionState
        {
            Entries = new[]
            {
                new AdditionEntry
                {
                    Id = "x", Name = "x", Command = "x", WorkingDir = "%V",
                    Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
                }
            }
        };
        applier.Apply(state);
        var afterFirst = reg.GetSubKeyNames(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell").ToList();
        applier.Apply(state);
        var afterSecond = reg.GetSubKeyNames(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell").ToList();
        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public void Apply_with_empty_state_purges_previous_owned_keys()
    {
        var reg = new FakeRegistry();
        var applier = new AdditionApplier(reg);
        applier.Apply(new AdditionState
        {
            Entries = new[]
            {
                new AdditionEntry
                {
                    Id = "leftover", Name = "leftover", Command = "x", WorkingDir = "%V",
                    Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
                }
            }
        });
        Assert.True(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.leftover"));
        applier.Apply(new AdditionState());
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser,
            "Software\\Classes\\Directory\\Background\\shell\\RCMM.leftover"));
    }
}
