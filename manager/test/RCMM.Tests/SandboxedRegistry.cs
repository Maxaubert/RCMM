using System.Collections.Generic;
using RCMM.Core.Services;

namespace RCMM.Tests;

/// <summary>
/// Prefixes every HKCU path with a test-owned sandbox root (Software\RCMMTests\&lt;tag&gt;)
/// before delegating to the real Win32Registry, so integration tests can exercise
/// real registry I/O without ever mutating the user's live HKCU state — a crashed
/// test host must not be able to leave hide markers or blocked-CLSID entries behind
/// in the real hives. Non-HKCU hives pass through untouched (integration tests only
/// write HKCU; HKCR/HKLM access is read-only machine inspection).
/// </summary>
internal sealed class SandboxedRegistry : IRegistry
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
    public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string path) => _inner.GetSubKeyNames(hive, Map(hive, path));
    public IReadOnlyList<string> GetValueNames(RegistryHive hive, string path) => _inner.GetValueNames(hive, Map(hive, path));
}
