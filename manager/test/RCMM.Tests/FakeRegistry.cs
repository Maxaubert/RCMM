using System;
using System.Collections.Generic;
using System.Linq;
using RCMM.Core.Services;

namespace RCMM.Tests;

/// <summary>
/// In-memory IRegistry that mirrors the two real-registry semantics production
/// code depends on (Win32Registry delegates to Microsoft.Win32):
///
///   1. Key paths and value names are case-insensitive — a scanner may
///      enumerate "directory\shell\Foo" and look it up as "Directory\shell\Foo".
///   2. HKCR is not a hive of its own but the OS-merged view of
///      HKCU\Software\Classes over HKLM\Software\Classes. Reads consult the
///      per-user branch first with per-value precedence (an HKCU value wins a
///      collision; a value present only in HKLM still shows through). Writes
///      land where a real HKCR write would: in the per-user instance when the
///      key already exists there, in the per-machine instance otherwise.
///
/// The old fake kept HKCR as a disjoint case-sensitive keyspace, so hide-state
/// detection that reads the merged view (AllTargetsHidden, the scanners' mask
/// recovery) silently exercised only its HKCU/HKLM fallback branches in tests.
/// Review finding b09-tests #19.
/// </summary>
public sealed class FakeRegistry : IRegistry
{
    private const string ClassesPrefix = @"Software\Classes";

    private sealed class KeyComparer : IEqualityComparer<(RegistryHive Hive, string Path)>
    {
        public bool Equals((RegistryHive Hive, string Path) x, (RegistryHive Hive, string Path) y)
            => x.Hive == y.Hive && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((RegistryHive Hive, string Path) k)
            => HashCode.Combine(k.Hive, StringComparer.OrdinalIgnoreCase.GetHashCode(k.Path));
    }

    // Only CurrentUser / LocalMachine paths are ever stored; every ClassesRoot
    // operation is translated to its backing Software\Classes instance first.
    private readonly Dictionary<(RegistryHive Hive, string Path), Dictionary<string, object>> _keys
        = new(new KeyComparer());

    public bool KeyExists(RegistryHive hive, string path)
    {
        path = Normalize(path);
        if (hive == RegistryHive.ClassesRoot)
            return _keys.ContainsKey(UserInstance(path)) || _keys.ContainsKey(MachineInstance(path));
        return _keys.ContainsKey((hive, path));
    }

    public void CreateKey(RegistryHive hive, string path)
    {
        (hive, path) = ResolveWrite(hive, Normalize(path));
        var parts = path.Split('\\');
        for (int i = 1; i <= parts.Length; i++)
        {
            var sub = string.Join('\\', parts[..i]);
            if (!_keys.ContainsKey((hive, sub)))
                _keys[(hive, sub)] = NewValues();
        }
    }

    public void DeleteKey(RegistryHive hive, string path)
    {
        (hive, path) = ResolveWrite(hive, Normalize(path));
        var doomed = _keys.Keys
            .Where(k => k.Hive == hive
                        && (string.Equals(k.Path, path, StringComparison.OrdinalIgnoreCase)
                            || k.Path.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var k in doomed) _keys.Remove(k);
    }

    public void DeleteValue(RegistryHive hive, string path, string name)
    {
        (hive, path) = ResolveWrite(hive, Normalize(path));
        if (_keys.TryGetValue((hive, path), out var values))
            values.Remove(name);
    }

    public object? GetValue(RegistryHive hive, string path, string name)
    {
        path = Normalize(path);
        if (hive == RegistryHive.ClassesRoot)
        {
            // Per-value merge with HKCU precedence — a value present only in the
            // machine instance shows through a per-user key that lacks it.
            if (_keys.TryGetValue(UserInstance(path), out var user) && user.TryGetValue(name, out var uv))
                return uv;
            return _keys.TryGetValue(MachineInstance(path), out var machine) && machine.TryGetValue(name, out var mv)
                ? mv : null;
        }
        return _keys.TryGetValue((hive, path), out var values) && values.TryGetValue(name, out var v) ? v : null;
    }

    public void SetValue(RegistryHive hive, string path, string name, object value)
    {
        (hive, path) = ResolveWrite(hive, Normalize(path));
        CreateKey(hive, path);
        _keys[(hive, path)][name] = value;
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string path)
    {
        path = Normalize(path);
        if (hive == RegistryHive.ClassesRoot)
        {
            var (uh, up) = UserInstance(path);
            var (mh, mp) = MachineInstance(path);
            return ChildNames(uh, up).Concat(ChildNames(mh, mp))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        return ChildNames(hive, path)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> GetValueNames(RegistryHive hive, string path)
    {
        path = Normalize(path);
        if (hive == RegistryHive.ClassesRoot)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_keys.TryGetValue(UserInstance(path), out var user)) names.UnionWith(user.Keys);
            if (_keys.TryGetValue(MachineInstance(path), out var machine)) names.UnionWith(machine.Keys);
            return names.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }
        return _keys.TryGetValue((hive, path), out var values)
            ? values.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList()
            : Array.Empty<string>();
    }

    private IEnumerable<string> ChildNames(RegistryHive hive, string path)
    {
        // Empty path = enumerate the hive root (Win32Registry opens the root key).
        var prefix = path.Length == 0 ? "" : path + "\\";
        return _keys.Keys
            .Where(k => k.Hive == hive && k.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Path[prefix.Length..])
            .Where(rest => rest.Length > 0 && !rest.Contains('\\'));
    }

    /// <summary>Translates a ClassesRoot mutation to the concrete instance a real
    /// HKCR write hits: the per-user key when it already exists, HKLM otherwise.</summary>
    private (RegistryHive Hive, string Path) ResolveWrite(RegistryHive hive, string path)
    {
        if (hive != RegistryHive.ClassesRoot) return (hive, path);
        var user = UserInstance(path);
        return _keys.ContainsKey(user) ? user : MachineInstance(path);
    }

    private static (RegistryHive Hive, string Path) UserInstance(string classesPath)
        => (RegistryHive.CurrentUser, Join(classesPath));

    private static (RegistryHive Hive, string Path) MachineInstance(string classesPath)
        => (RegistryHive.LocalMachine, Join(classesPath));

    private static string Join(string classesPath)
        => classesPath.Length == 0 ? ClassesPrefix : ClassesPrefix + "\\" + classesPath;

    private static Dictionary<string, object> NewValues() => new(StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Trim('\\');
}
