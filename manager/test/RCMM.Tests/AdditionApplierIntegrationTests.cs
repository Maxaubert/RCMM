using System;
using System.IO;
using System.Linq;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Tests;

/// <summary>
/// End-to-end smoke test against the real Win32Registry. Writes RCMM.IT_*
/// keys into HKCU and verifies them, then tears them down. Should produce
/// no residue. If a test fails mid-way the cleanup in Dispose still runs.
/// </summary>
public sealed class AdditionApplierIntegrationTests : IDisposable
{
    private readonly Win32Registry _reg = new();
    private readonly string _testTag = "IT_" + Guid.NewGuid().ToString("N").Substring(0, 8);

    [Fact]
    public void Apply_writes_and_reads_back_a_real_entry()
    {
        var applier = new AdditionApplier(_reg);
        var entry = new AdditionEntry
        {
            Id = _testTag,
            Name = "RCMM integration test " + _testTag,
            Command = "echo integration",
            WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground,
            RunMode = RunMode.VisibleTerminal,
        };
        applier.Apply(new AdditionState { Entries = new[] { entry } });

        var verbPath = "Software\\Classes\\Directory\\Background\\shell\\RCMM." + _testTag;
        Assert.True(_reg.KeyExists(RegistryHive.CurrentUser, verbPath),
            "real registry should contain the verb key");
        Assert.Equal("RCMM integration test " + _testTag,
            _reg.GetValue(RegistryHive.CurrentUser, verbPath, "") as string);
        Assert.Equal("cmd /k echo integration",
            _reg.GetValue(RegistryHive.CurrentUser, verbPath + "\\command", "") as string);
    }

    [Fact]
    public void Apply_then_empty_state_removes_real_keys()
    {
        var applier = new AdditionApplier(_reg);
        var entry = new AdditionEntry
        {
            Id = _testTag + "_delete",
            Name = "delete me", Command = "x", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
        };
        applier.Apply(new AdditionState { Entries = new[] { entry } });
        var verbPath = "Software\\Classes\\Directory\\Background\\shell\\RCMM." + _testTag + "_delete";
        Assert.True(_reg.KeyExists(RegistryHive.CurrentUser, verbPath));

        applier.Apply(new AdditionState());
        Assert.False(_reg.KeyExists(RegistryHive.CurrentUser, verbPath),
            "subsequent empty Apply should have purged the previous key");
    }

    [Fact]
    public void Apply_writes_real_submenu_with_ExtendedSubCommandsKey()
    {
        var applier = new AdditionApplier(_reg);
        var folderId = _testTag + "_folder";
        var folder = new AdditionFolder { Id = folderId, Name = "IT Folder" };
        var child = new AdditionEntry
        {
            Id = _testTag + "_child",
            Name = "IT Child", Command = "echo child", WorkingDir = "%V",
            Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
            FolderId = folderId,
        };
        applier.Apply(new AdditionState { Folders = new[] { folder }, Entries = new[] { child } });

        var parentPath = "Software\\Classes\\Directory\\Background\\shell\\RCMM." + folderId;
        Assert.True(_reg.KeyExists(RegistryHive.CurrentUser, parentPath));
        Assert.Equal("Directory\\Background\\ContextMenus\\RCMM." + folderId,
            _reg.GetValue(RegistryHive.CurrentUser, parentPath, "ExtendedSubCommandsKey") as string);
        var childPath = "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM." + folderId
                        + "\\shell\\RCMM." + _testTag + "_child";
        Assert.True(_reg.KeyExists(RegistryHive.CurrentUser, childPath));
    }

    /// <summary>
    /// Belt and braces cleanup: removes every RCMM.IT_* key under each scope-root
    /// we may have written into. Safe to call even if no test wrote anything.
    /// </summary>
    public void Dispose()
    {
        foreach (var scopeRoot in new[]
                 {
                     "Software\\Classes\\Directory\\Background\\shell",
                     "Software\\Classes\\Directory\\Background\\ContextMenus",
                 })
        {
            try
            {
                if (!_reg.KeyExists(RegistryHive.CurrentUser, scopeRoot)) continue;
                foreach (var name in _reg.GetSubKeyNames(RegistryHive.CurrentUser, scopeRoot))
                {
                    if (!name.StartsWith("RCMM." + _testTag, StringComparison.Ordinal)) continue;
                    _reg.DeleteKey(RegistryHive.CurrentUser, scopeRoot + "\\" + name);
                }
            }
            catch { /* best-effort cleanup */ }
        }
    }
}
