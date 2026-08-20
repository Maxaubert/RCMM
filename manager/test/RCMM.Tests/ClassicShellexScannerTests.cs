using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class ClassicShellexScannerTests
{
    [Fact]
    public void Scan_returns_empty_when_no_handlers_key()
    {
        var reg = new FakeRegistry();
        var sut = new ClassicShellexScanner(reg, new ClsidResolver(reg), new FakeFileVersionReader());
        Assert.Empty(sut.Scan(Scope.Files));
    }

    [Fact]
    public void Scan_finds_handler_by_clsid_default_value()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\WinRARShell", "", "{ABC}");
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{ABC}", "", "WinRAR Shell");
        var sut = new ClassicShellexScanner(reg, new ClsidResolver(reg), new FakeFileVersionReader());

        var entries = sut.Scan(Scope.Files).ToList();
        Assert.Single(entries);
        Assert.Equal("WinRARShell", entries[0].OriginalKeyName);
        Assert.Equal("{ABC}", entries[0].Clsid);
        Assert.Equal(EntryKind.ShellExtension, entries[0].Kind);
    }

    [Fact]
    public void Scan_uses_clsid_defaultName_when_key_name_is_clsid()
    {
        var reg = new FakeRegistry();
        reg.CreateKey(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\{XYZ}");
        // Provide a DefaultName so the entry is not dropped.
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{XYZ}", "", "Some Extension");
        var sut = new ClassicShellexScanner(reg, new ClsidResolver(reg), new FakeFileVersionReader());

        var result = sut.Scan(Scope.Files).Single();
        Assert.Equal("{XYZ}", result.Clsid);
        // DefaultName "Some Extension" != key name "{XYZ}" → used as display
        Assert.Equal("Some Extension", result.DisplayName);
    }

    [Fact]
    public void Scan_drops_handler_with_no_friendly_name()
    {
        var reg = new FakeRegistry();
        // Key name is the only potential identifier; no CLSID resolved, no FileDescription, no DefaultName.
        reg.CreateKey(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\EPP");
        var sut = new ClassicShellexScanner(reg, new ClsidResolver(reg), new FakeFileVersionReader());

        // Entry should be dropped because PickDisplay returns null.
        Assert.Empty(sut.Scan(Scope.Files));
    }

    [Fact]
    public void Scan_drops_handler_when_defaultName_equals_key_name()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\MyExt", "", "{ZZZ}");
        // DefaultName exactly matches key name (case-insensitive) → treated as no meaningful name.
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{ZZZ}", "", "MyExt");
        var sut = new ClassicShellexScanner(reg, new ClsidResolver(reg), new FakeFileVersionReader());

        Assert.Empty(sut.Scan(Scope.Files));
    }

    [Fact]
    public void Scan_recovers_masked_per_user_only_handler_via_SavedDefault_stash()
    {
        // A hidden per-user-only handler: no HKLM twin, key name not a CLSID, the
        // merged HKCR default is the mask's empty string. The CLSID only survives
        // in the RCMM.SavedDefault stash Hide wrote — without reading it the row
        // is dropped (no display name) and the user can never un-hide the handler.
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\MyTool", "", "");
        reg.SetValue(RegistryHive.CurrentUser,
            @"Software\Classes\*\shellex\ContextMenuHandlers\MyTool", "", "");
        reg.SetValue(RegistryHive.CurrentUser,
            @"Software\Classes\*\shellex\ContextMenuHandlers\MyTool",
            HideService.SavedDefaultValueName, "{ABC}");
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{ABC}", "", "My Tool Extension");
        var sut = new ClassicShellexScanner(reg, new ClsidResolver(reg), new FakeFileVersionReader());

        // Hidden-state itself is computed downstream by MainViewModel's
        // AllTargetsHidden — the scanner's job is only to keep the row (and its
        // CLSID) discoverable so the user can still un-hide it.
        var entry = sut.Scan(Scope.Files).Single();
        Assert.Equal("{ABC}", entry.Clsid);
        Assert.Equal("My Tool Extension", entry.DisplayName);
    }
}
