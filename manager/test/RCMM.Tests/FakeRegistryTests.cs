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

    // ---- Real-registry fidelity: case-insensitivity (review b09 #19) ----

    [Fact]
    public void Key_paths_are_case_insensitive_like_the_real_registry()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.LocalMachine, @"Software\Foo\Bar", "Name", "v");

        Assert.True(reg.KeyExists(RegistryHive.LocalMachine, @"SOFTWARE\foo\BAR"));
        Assert.Equal("v", reg.GetValue(RegistryHive.LocalMachine, @"software\FOO\bar", "Name"));
    }

    [Fact]
    public void Value_names_are_case_insensitive_like_the_real_registry()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.CurrentUser, @"Software\Foo", "LegacyDisable", "");

        Assert.NotNull(reg.GetValue(RegistryHive.CurrentUser, @"Software\Foo", "legacydisable"));
        reg.DeleteValue(RegistryHive.CurrentUser, @"Software\Foo", "LEGACYDISABLE");
        Assert.Null(reg.GetValue(RegistryHive.CurrentUser, @"Software\Foo", "LegacyDisable"));
    }

    [Fact]
    public void CreateKey_with_different_casing_does_not_duplicate_the_key()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.LocalMachine, @"Software\Root\Child");
        reg.CreateKey(RegistryHive.LocalMachine, @"Software\Root\CHILD");

        Assert.Single(reg.GetSubKeyNames(RegistryHive.LocalMachine, @"Software\Root"));
    }

    // ---- Real-registry fidelity: HKCR is the HKCU-over-HKLM merged view ----

    [Fact]
    public void HKCR_read_sees_a_key_written_to_HKLM_Software_Classes()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.LocalMachine, @"Software\Classes\Directory\shell\foo", "", "Foo");

        Assert.True(reg.KeyExists(RegistryHive.ClassesRoot, @"Directory\shell\foo"));
        Assert.Equal("Foo", reg.GetValue(RegistryHive.ClassesRoot, @"Directory\shell\foo", ""));
    }

    [Fact]
    public void HKCR_read_sees_a_key_written_to_HKCU_Software_Classes()
    {
        // The exact merge HideService relies on: a per-user LegacyDisable must be
        // visible through HKCR, otherwise hide-state detection tests only ever
        // exercised the HKCU fallback branch.
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.CurrentUser, @"Software\Classes\Directory\shell\foo", "LegacyDisable", "");

        Assert.NotNull(reg.GetValue(RegistryHive.ClassesRoot, @"Directory\shell\foo", "LegacyDisable"));
    }

    [Fact]
    public void HKCR_read_prefers_the_HKCU_value_over_the_HKLM_value()
    {
        // The HkcuMask mechanism: an empty per-user default shadows the
        // machine registration's CLSID in the merged view.
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.LocalMachine, @"Software\Classes\Directory\shellex\ContextMenuHandlers\X", "", "{CLSID}");
        reg.SetValue(RegistryHive.CurrentUser, @"Software\Classes\Directory\shellex\ContextMenuHandlers\X", "", "");

        Assert.Equal("", reg.GetValue(RegistryHive.ClassesRoot, @"Directory\shellex\ContextMenuHandlers\X", ""));
    }

    [Fact]
    public void HKCR_read_falls_through_to_HKLM_for_a_value_the_HKCU_key_lacks()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.LocalMachine, @"Software\Classes\Directory\shell\foo", "Icon", "app.exe");
        reg.SetValue(RegistryHive.CurrentUser, @"Software\Classes\Directory\shell\foo", "LegacyDisable", "");

        Assert.Equal("app.exe", reg.GetValue(RegistryHive.ClassesRoot, @"Directory\shell\foo", "Icon"));
    }

    [Fact]
    public void HKCR_write_lands_in_HKLM_when_no_per_user_key_exists()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\foo", "", "Foo");

        Assert.Equal("Foo", reg.GetValue(RegistryHive.LocalMachine, @"Software\Classes\Directory\shell\foo", ""));
        Assert.False(reg.KeyExists(RegistryHive.CurrentUser, @"Software\Classes\Directory\shell\foo"));
    }

    [Fact]
    public void HKCR_write_lands_in_HKCU_when_the_per_user_key_already_exists()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.CurrentUser, @"Software\Classes\Directory\shell\foo");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\foo", "", "Foo");

        Assert.Equal("Foo", reg.GetValue(RegistryHive.CurrentUser, @"Software\Classes\Directory\shell\foo", ""));
        Assert.Null(reg.GetValue(RegistryHive.LocalMachine, @"Software\Classes\Directory\shell\foo", ""));
    }

    [Fact]
    public void HKCR_subkey_enumeration_unions_both_branches()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.LocalMachine, @"Software\Classes\Directory\shell\FromMachine");
        reg.CreateKey(RegistryHive.CurrentUser, @"Software\Classes\Directory\shell\FromUser");
        reg.CreateKey(RegistryHive.CurrentUser, @"Software\Classes\Directory\shell\FromMachine"); // both

        var names = reg.GetSubKeyNames(RegistryHive.ClassesRoot, @"Directory\shell");
        Assert.Equal(new[] { "FromMachine", "FromUser" }, names);
    }
}
