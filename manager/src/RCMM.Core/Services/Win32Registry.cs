using System;
using System.Collections.Generic;
using Win32 = Microsoft.Win32;

namespace RCMM.Core.Services;

public sealed class Win32Registry : IRegistry
{
    public bool KeyExists(RegistryHive hive, string path)
    {
        using var k = OpenSubKey(hive, path, writable: false);
        return k != null;
    }

    public void CreateKey(RegistryHive hive, string path)
    {
        using var k = Root(hive).CreateSubKey(path, writable: true);
    }

    public void DeleteKey(RegistryHive hive, string path)
    {
        Root(hive).DeleteSubKeyTree(path, throwOnMissingSubKey: false);
    }

    public void DeleteValue(RegistryHive hive, string path, string name)
    {
        using var k = Root(hive).OpenSubKey(path, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    public object? GetValue(RegistryHive hive, string path, string name)
    {
        using var k = OpenSubKey(hive, path, writable: false);
        return k?.GetValue(name);
    }

    public void SetValue(RegistryHive hive, string path, string name, object value)
    {
        using var k = Root(hive).CreateSubKey(path, writable: true);
        k.SetValue(name, value);
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string path)
    {
        using var k = OpenSubKey(hive, path, writable: false);
        return k?.GetSubKeyNames() ?? Array.Empty<string>();
    }

    public IReadOnlyList<string> GetValueNames(RegistryHive hive, string path)
    {
        using var k = OpenSubKey(hive, path, writable: false);
        return k?.GetValueNames() ?? Array.Empty<string>();
    }

    private static Win32.RegistryKey Root(RegistryHive hive) => hive switch
    {
        RegistryHive.ClassesRoot  => Win32.Registry.ClassesRoot,
        RegistryHive.CurrentUser  => Win32.Registry.CurrentUser,
        RegistryHive.LocalMachine => Win32.Registry.LocalMachine,
        _ => throw new ArgumentOutOfRangeException(nameof(hive))
    };

    // Empty path = the hive root itself (used to enumerate HKCR's top level).
    // Disposing a system root key is a documented no-op, so the callers' `using`
    // blocks are safe on the returned root.
    private static Win32.RegistryKey? OpenSubKey(RegistryHive hive, string path, bool writable)
        => path.Length == 0 ? Root(hive) : Root(hive).OpenSubKey(path, writable);
}
