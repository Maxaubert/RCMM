using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

/// <summary>
/// Pins the ShellexNameIndex behavior over the shared ContextMenuHandlers walk
/// (review b10 finding 48): registrations resolve through the one walker, and
/// the index deliberately does NOT recover CLSIDs masked by RCMM's HkcuMask —
/// a masked handler has no live capture to attach a name to, and its recovery
/// path is ClassicShellexScanner's.
/// </summary>
public class ShellexNameIndexTests
{
    private const string Clsid = "{11111111-2222-3333-4444-555555555555}";

    private static ShellexNameIndex MakeSut(FakeRegistry reg)
        => new ShellexNameIndex(reg, new ClsidResolver(reg), new FakeFileVersionReader());

    [Fact]
    public void BuildNameToClsidMap_indexes_active_registration_by_resolved_display_name()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\MyTool", "", Clsid);
        reg.SetValue(RegistryHive.ClassesRoot, $@"CLSID\{Clsid}", "", "My Tool Extension");

        var map = MakeSut(reg).BuildNameToClsidMap();

        Assert.Equal(Clsid.ToUpperInvariant(), map["My Tool Extension"]);
    }

    [Fact]
    public void BuildNameToClsidMap_skips_short_brace_string_registrations()
    {
        // "{broken}" is a malformed registration, not a CLSID — the strict
        // shared LooksLikeClsid keeps it out of the whole pipeline.
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shellex\ContextMenuHandlers\Broken", "", "{broken}");
        reg.SetValue(RegistryHive.ClassesRoot, @"CLSID\{broken}", "", "Broken Extension");

        var map = MakeSut(reg).BuildNameToClsidMap();

        Assert.Empty(map);
    }

    [Fact]
    public void BuildNameToClsidMap_does_not_recover_masked_registrations()
    {
        // The merged-HKCR default reads back empty because RCMM's HkcuMask
        // shadow wins; the real CLSID survives only in HKLM. The scanner
        // recovers it (so the hidden row stays toggleable) — this index
        // deliberately does not: a masked handler contributes no live capture
        // that would need the name attach.
        var reg = new FakeRegistry();
        // Real registration at HKLM; RCMM's HkcuMask shadow (empty default) at
        // HKCU wins in the merged HKCR view.
        reg.SetValue(RegistryHive.LocalMachine, @"Software\Classes\*\shellex\ContextMenuHandlers\MyTool", "", Clsid);
        reg.SetValue(RegistryHive.CurrentUser, @"Software\Classes\*\shellex\ContextMenuHandlers\MyTool", "", "");
        reg.SetValue(RegistryHive.ClassesRoot, $@"CLSID\{Clsid}", "", "My Tool Extension");

        var map = MakeSut(reg).BuildNameToClsidMap();

        Assert.Empty(map);
    }
}
