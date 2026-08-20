using System;
using System.IO;
using System.Linq;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Tests;

/// <summary>
/// End-to-end smoke test against the real Win32Registry — but remapped into a
/// throwaway sandbox subtree (Software\RCMMTests\&lt;tag&gt;\…) so Apply's purge can
/// never touch the machine's real Software\Classes. Apply purges every RCMM.*
/// key under every scope root it knows (including, since #23, all extension
/// roots it can enumerate), so running it against the real Classes tree would
/// delete the developer's live RCMM additions. Dispose removes the whole
/// sandbox subtree.
/// </summary>
public sealed class AdditionApplierIntegrationTests : IDisposable
{
    private readonly string _testTag = "IT_" + Guid.NewGuid().ToString("N").Substring(0, 8);
    private readonly string _sandbox;
    private readonly Win32Registry _real = new();
    private readonly IRegistry _reg;

    public AdditionApplierIntegrationTests()
    {
        _sandbox = "Software\\RCMMTests\\" + _testTag;
        _reg = new SandboxedRegistry(_real, _sandbox);
    }

    [Fact]
    public void Apply_writes_and_reads_back_a_real_entry()
    {
        var applier = new AdditionApplier(_reg);
        applier.Apply(new AdditionState
        {
            Entries = new[]
            {
                new AdditionEntry
                {
                    Id = _testTag, Name = "RCMM integration test " + _testTag,
                    Command = "echo integration", WorkingDir = "%V",
                    Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
                }
            }
        });

        var verbPath = "Software\\Classes\\Directory\\Background\\shell\\RCMM.001." + _testTag;
        Assert.True(_reg.KeyExists(RegistryHive.CurrentUser, verbPath), "real registry should contain the verb key");
        Assert.Equal("RCMM integration test " + _testTag,
            _reg.GetValue(RegistryHive.CurrentUser, verbPath, "") as string);
        Assert.Equal("cmd /k echo integration",
            _reg.GetValue(RegistryHive.CurrentUser, verbPath + "\\command", "") as string);
    }

    [Fact]
    public void Apply_then_empty_state_removes_real_keys()
    {
        var applier = new AdditionApplier(_reg);
        applier.Apply(new AdditionState
        {
            Entries = new[]
            {
                new AdditionEntry
                {
                    Id = _testTag + "_delete",
                    Name = "delete me", Command = "x", WorkingDir = "%V",
                    Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
                }
            }
        });
        var verbPath = "Software\\Classes\\Directory\\Background\\shell\\RCMM.001." + _testTag + "_delete";
        Assert.True(_reg.KeyExists(RegistryHive.CurrentUser, verbPath));

        applier.Apply(new AdditionState());
        Assert.False(_reg.KeyExists(RegistryHive.CurrentUser, verbPath));
    }

    [Fact]
    public void Apply_writes_real_submenu_with_ExtendedSubCommandsKey()
    {
        var applier = new AdditionApplier(_reg);
        var folderId = _testTag + "_folder";
        applier.Apply(new AdditionState
        {
            Folders = new[] { new AdditionFolder { Id = folderId, Name = "IT Folder" } },
            Entries = new[]
            {
                new AdditionEntry
                {
                    Id = _testTag + "_child",
                    Name = "IT Child", Command = "echo child", WorkingDir = "%V",
                    Scope = AdditionScope.FolderBackground, RunMode = RunMode.VisibleTerminal,
                    FolderId = folderId,
                }
            }
        });

        var parentPath = "Software\\Classes\\Directory\\Background\\shell\\RCMM.001." + folderId;
        Assert.True(_reg.KeyExists(RegistryHive.CurrentUser, parentPath));
        Assert.Equal("Directory\\Background\\ContextMenus\\RCMM.001." + folderId,
            _reg.GetValue(RegistryHive.CurrentUser, parentPath, "ExtendedSubCommandsKey") as string);
        var childPath = "Software\\Classes\\Directory\\Background\\ContextMenus\\RCMM.001." + folderId
                        + "\\shell\\RCMM.001." + _testTag + "_child";
        Assert.True(_reg.KeyExists(RegistryHive.CurrentUser, childPath));
    }

    /// <summary>Deletes only this instance's sandbox subtree. The Software\RCMMTests
    /// root is now shared with other integration test classes (ApplyEndToEndTests)
    /// that xUnit runs in parallel, so sweeping the whole root here would delete a
    /// concurrently running class's live sandbox mid-test.</summary>
    public void Dispose()
    {
        try { _real.DeleteKey(RegistryHive.CurrentUser, _sandbox); }
        catch { /* best-effort cleanup */ }
    }
}
