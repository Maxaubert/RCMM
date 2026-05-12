using System.Linq;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class CommandStoreVerbIndexTests
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell";

    [Fact]
    public void Empty_registry_yields_empty_map()
    {
        var reg = new FakeRegistry();
        var sut = new CommandStoreVerbIndex(reg);
        Assert.Empty(sut.Build());
    }

    [Fact]
    public void Index_picks_up_ExplorerCommandHandler_clsid()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.LocalMachine,
            Root + @"\Windows.share", "ExplorerCommandHandler",
            "{99D353BC-C813-41ec-8F28-EAE61E702E57}");

        var sut = new CommandStoreVerbIndex(reg);

        Assert.Contains("{99D353BC-C813-41EC-8F28-EAE61E702E57}", sut.LookupClsids("Windows.share"));
        Assert.Contains("{99D353BC-C813-41EC-8F28-EAE61E702E57}", sut.LookupClsids("share"));
        Assert.Contains("{99D353BC-C813-41EC-8F28-EAE61E702E57}", sut.LookupClsids("Windows.Share"));
    }

    [Fact]
    public void Index_picks_up_VerbHandler_when_ExplorerCommandHandler_absent()
    {
        var reg = new FakeRegistry();
        // Real shape from Windows.copyaspath — VerbHandler is what activates the verb.
        reg.SetValue(RegistryHive.LocalMachine,
            Root + @"\Windows.copyaspath", "VerbHandler",
            "{f3d06e7c-1e45-4a26-847e-f9fcdee59be0}");

        var sut = new CommandStoreVerbIndex(reg);
        Assert.Contains("{F3D06E7C-1E45-4A26-847E-F9FCDEE59BE0}", sut.LookupClsids("copyaspath"));
    }

    [Fact]
    public void Index_unions_multiple_handlers_per_entry()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.LocalMachine,
            Root + @"\Windows.copyaspath", "VerbHandler",
            "{f3d06e7c-1e45-4a26-847e-f9fcdee59be0}");
        reg.SetValue(RegistryHive.LocalMachine,
            Root + @"\Windows.copyaspath", "CommandStateHandler",
            "{3B1599F9-E00A-4BBF-AD3E-B3F99FA87779}");

        var sut = new CommandStoreVerbIndex(reg);
        var clsids = sut.LookupClsids("copyaspath").ToList();

        Assert.Contains("{F3D06E7C-1E45-4A26-847E-F9FCDEE59BE0}", clsids);
        Assert.Contains("{3B1599F9-E00A-4BBF-AD3E-B3F99FA87779}", clsids);
    }

    [Fact]
    public void Lookup_returns_empty_for_unknown_verb()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.LocalMachine,
            Root + @"\Windows.share", "ExplorerCommandHandler",
            "{99D353BC-C813-41ec-8F28-EAE61E702E57}");

        var sut = new CommandStoreVerbIndex(reg);
        Assert.Empty(sut.LookupClsids("NoSuchVerb"));
    }

    [Fact]
    public void Entries_without_any_handler_value_are_skipped()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.LocalMachine,
            Root + @"\Windows.Cancel", "CommandStateSync", "");

        var sut = new CommandStoreVerbIndex(reg);
        Assert.Empty(sut.Build());
    }

    [Fact]
    public void Non_clsid_handler_values_are_skipped()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.LocalMachine,
            Root + @"\Windows.cmd", "ExplorerCommandHandler",
            "shell32.dll");

        var sut = new CommandStoreVerbIndex(reg);
        Assert.Empty(sut.LookupClsids("Windows.cmd"));
    }
}
