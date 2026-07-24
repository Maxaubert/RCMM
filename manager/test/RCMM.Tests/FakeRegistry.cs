using System;
using System.Collections.Generic;
using System.Linq;
using RCMM.Core.Services;

namespace RCMM.Tests;

public sealed class FakeRegistry : IRegistry
{
    private readonly Dictionary<(RegistryHive Hive, string Path), Dictionary<string, object>> _keys = new();

    public bool KeyExists(RegistryHive hive, string path) => _keys.ContainsKey((hive, Normalize(path)));

    public void CreateKey(RegistryHive hive, string path)
    {
        path = Normalize(path);
        var parts = path.Split('\\');
        for (int i = 1; i <= parts.Length; i++)
        {
            var sub = string.Join('\\', parts[..i]);
            if (!_keys.ContainsKey((hive, sub)))
                _keys[(hive, sub)] = new Dictionary<string, object>();
        }
    }

    public void DeleteKey(RegistryHive hive, string path)
    {
        path = Normalize(path);
        var doomed = _keys.Keys
            .Where(k => k.Hive == hive && (k.Path == path || k.Path.StartsWith(path + "\\")))
            .ToList();
        foreach (var k in doomed) _keys.Remove(k);
    }

    public void DeleteValue(RegistryHive hive, string path, string name)
    {
        if (_keys.TryGetValue((hive, Normalize(path)), out var values))
            values.Remove(name);
    }

    public object? GetValue(RegistryHive hive, string path, string name)
        => _keys.TryGetValue((hive, Normalize(path)), out var values) && values.TryGetValue(name, out var v) ? v : null;

    public void SetValue(RegistryHive hive, string path, string name, object value)
    {
        CreateKey(hive, path);
        _keys[(hive, Normalize(path))][name] = value;
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string path)
    {
        path = Normalize(path);
        // Empty path = enumerate the hive root (Win32Registry opens the root key).
        var prefix = path.Length == 0 ? "" : path + "\\";
        return _keys.Keys
            .Where(k => k.Hive == hive && k.Path.StartsWith(prefix))
            .Select(k => k.Path[prefix.Length..])
            .Where(rest => !rest.Contains('\\'))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> GetValueNames(RegistryHive hive, string path)
        => _keys.TryGetValue((hive, Normalize(path)), out var values)
            ? values.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList()
            : Array.Empty<string>();

    private static string Normalize(string path) => path.Trim('\\');
}
