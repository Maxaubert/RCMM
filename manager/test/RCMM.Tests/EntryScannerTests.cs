using System.Linq;
using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class EntryScannerTests
{
    [Fact]
    public void ScanAll_combines_verbs_and_shellex_across_scopes()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\foo", "", "Foo");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shellex\ContextMenuHandlers\Bar", "", "{X}");

        var sut = new EntryScanner(
            new ClassicVerbScanner(reg),
            new ClassicShellexScanner(reg, new ClsidResolver(reg)));

        var all = sut.ScanAll().ToList();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, e => e.Scope == Scope.Files && e.Kind == EntryKind.ShellVerb);
        Assert.Contains(all, e => e.Scope == Scope.Folders && e.Kind == EntryKind.ShellExtension);
    }

    [Fact]
    public void ScanScope_filters_to_that_scope_only()
    {
        var reg = new FakeRegistry();
        reg.SetValue(RegistryHive.ClassesRoot, @"*\shell\a", "", "A");
        reg.SetValue(RegistryHive.ClassesRoot, @"Directory\shell\b", "", "B");

        var sut = new EntryScanner(
            new ClassicVerbScanner(reg),
            new ClassicShellexScanner(reg, new ClsidResolver(reg)));

        var files = sut.ScanScope(Scope.Files).ToList();
        Assert.Single(files);
        Assert.Equal("A", files[0].DisplayName);
    }
}
