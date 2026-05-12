using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class ClsidResolverTests
{
    [Fact]
    public void Resolve_returns_null_for_unknown_clsid()
    {
        var reg = new FakeRegistry();
        var sut = new ClsidResolver(reg);
        Assert.Null(sut.Resolve("{DEAD-BEEF}"));
    }

    [Fact]
    public void Resolve_returns_dll_path_and_default_name()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{ABC}\InprocServer32", "", @"C:\Path\my.dll");
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{ABC}", "", "FriendlyName");
        var sut = new ClsidResolver(reg);

        var info = sut.Resolve("{ABC}");
        Assert.NotNull(info);
        Assert.Equal(@"C:\Path\my.dll", info!.DllPath);
        Assert.Equal("FriendlyName", info.DefaultName);
    }

    [Fact]
    public void Resolve_handles_missing_default_name()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{ABC}\InprocServer32", "", @"C:\Path\my.dll");
        reg.CreateKey(RegistryHive.ClassesRoot, @"CLSID\{ABC}");
        var sut = new ClsidResolver(reg);

        var info = sut.Resolve("{ABC}");
        Assert.NotNull(info);
        Assert.Null(info!.DefaultName);
    }
}
