namespace RCMM.Core.Services;

public sealed record ClsidInfo(string Clsid, string? DllPath, string? DefaultName);

public sealed class ClsidResolver
{
    private readonly IRegistry _reg;

    public ClsidResolver(IRegistry reg) { _reg = reg; }

    public ClsidInfo? Resolve(string clsid)
    {
        var keyPath = $@"CLSID\{clsid}";
        if (!_reg.KeyExists(RegistryHive.ClassesRoot, keyPath)) return null;

        var dll = _reg.GetValue(RegistryHive.ClassesRoot, $@"CLSID\{clsid}\InprocServer32", "") as string;
        var defaultName = _reg.GetValue(RegistryHive.ClassesRoot, keyPath, "") as string;
        return new ClsidInfo(clsid, dll, defaultName);
    }
}
