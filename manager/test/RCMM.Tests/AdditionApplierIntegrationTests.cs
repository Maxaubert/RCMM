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

    /// <summary>Prefixes every HKCU path with the sandbox root before delegating
    /// to the real Win32Registry. Non-HKCU hives pass through untouched (the
    /// applier only writes HKCU; reads of HKCR/HKLM are not part of these tests).</summary>
    private sealed class SandboxedRegistry : IRegistry
    {
        private readonly Win32Registry _inner;
        private readonly string _root;
        public SandboxedRegistry(Win32Registry inner, string root) { _inner = inner; _root = root; }
        private string Map(RegistryHive hive, string path)
            => hive == RegistryHive.CurrentUser ? (path.Length == 0 ? _root : _root + "\\" + path) : path;
        public bool KeyExists(RegistryHive hive, string path) => _inner.KeyExists(hive, Map(hive, path));
        public void CreateKey(RegistryHive hive, string path) => _inner.CreateKey(hive, Map(hive, path));
        public void DeleteKey(RegistryHive hive, string path) => _inner.DeleteKey(hive, Map(hive, path));
        public void DeleteValue(RegistryHive hive, string path, string name) => _inner.DeleteValue(hive, Map(hive, path), name);
        public object? GetValue(RegistryHive hive, string path, string name) => _inner.GetValue(hive, Map(hive, path), name);
        public void SetValue(RegistryHive hive, string path, string name, object value) => _inner.SetValue(hive, Map(hive, path), name, value);
        public System.Collections.Generic.IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string path) => _inner.GetSubKeyNames(hive, Map(hive, path));
        public System.Collections.Generic.IReadOnlyList<string> GetValueNames(RegistryHive hive, string path) => _inner.GetValueNames(hive, Map(hive, path));
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

    /// <summary>Deletes the shared sandbox root in one recursive sweep — this also
    /// clears residue from earlier crashed runs. Instances of a single xUnit test
    /// class never run concurrently, so removing the shared root is race-free.</summary>
    public void Dispose()
    {
        try { _real.DeleteKey(RegistryHive.CurrentUser, "Software\\RCMMTests"); }
        catch { /* best-effort cleanup */ }
    }
}
