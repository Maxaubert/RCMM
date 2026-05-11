using System.Collections.Generic;

namespace RCMM.Core.Services;

public enum RegistryHive { ClassesRoot, CurrentUser, LocalMachine }

public interface IRegistry
{
    bool KeyExists(RegistryHive hive, string path);
    void CreateKey(RegistryHive hive, string path);
    void DeleteKey(RegistryHive hive, string path);
    void DeleteValue(RegistryHive hive, string path, string name);
    object? GetValue(RegistryHive hive, string path, string name);
    void SetValue(RegistryHive hive, string path, string name, object value);
    IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string path);
    IReadOnlyList<string> GetValueNames(RegistryHive hive, string path);
}
