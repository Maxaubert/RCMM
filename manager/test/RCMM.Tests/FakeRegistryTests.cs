using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class FakeRegistryTests
{
    [Fact]
    public void KeyExists_returns_false_for_unknown_path()
    {
        var reg = new FakeRegistry();
        Assert.False(reg.KeyExists(RegistryHive.ClassesRoot, @"Foo\Bar"));
    }

    [Fact]
    public void CreateKey_then_KeyExists_returns_true()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"Foo\Bar");
        Assert.True(reg.KeyExists(RegistryHive.ClassesRoot, @"Foo\Bar"));
    }

    [Fact]
    public void SetValue_then_GetValue_returns_value()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"Foo");
        reg.SetValue(RegistryHive.ClassesRoot, @"Foo", "Name", "Hello");
        Assert.Equal("Hello", reg.GetValue(RegistryHive.ClassesRoot, @"Foo", "Name"));
    }

    [Fact]
    public void GetValue_returns_null_for_unknown_value()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"Foo");
        Assert.Null(reg.GetValue(RegistryHive.ClassesRoot, @"Foo", "Missing"));
    }

    [Fact]
    public void GetSubKeyNames_lists_immediate_children_only()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"Root\A");
        reg.CreateKey(RegistryHive.ClassesRoot, @"Root\B");
        reg.CreateKey(RegistryHive.ClassesRoot, @"Root\B\Nested");

        var names = reg.GetSubKeyNames(RegistryHive.ClassesRoot, @"Root");
        Assert.Equal(new[] { "A", "B" }, names);
    }

    [Fact]
    public void DeleteValue_removes_value_but_keeps_key()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"Foo");
        reg.SetValue(RegistryHive.ClassesRoot, @"Foo", "X", "Y");
        reg.DeleteValue(RegistryHive.ClassesRoot, @"Foo", "X");
        Assert.True(reg.KeyExists(RegistryHive.ClassesRoot, @"Foo"));
        Assert.Null(reg.GetValue(RegistryHive.ClassesRoot, @"Foo", "X"));
    }

    [Fact]
    public void DeleteKey_recurses()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"Foo\Bar\Baz");
        reg.DeleteKey(RegistryHive.ClassesRoot, @"Foo");
        Assert.False(reg.KeyExists(RegistryHive.ClassesRoot, @"Foo"));
        Assert.False(reg.KeyExists(RegistryHive.ClassesRoot, @"Foo\Bar"));
    }
}
