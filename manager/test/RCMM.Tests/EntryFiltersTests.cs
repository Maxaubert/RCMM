using RCMM.Core.Util;
using Xunit;

namespace RCMM.Tests;

public class EntryFiltersTests
{
    [Theory]
    [InlineData("Open Git Bash here")]
    [InlineData("Edit with Notepad")]
    [InlineData("WinRAR")]
    [InlineData("Add to Visual Studio")]
    [InlineData("a")]
    public void Accepts_friendly_names(string name)
        => Assert.True(EntryFilters.IsLikelyUserVisible(name));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_empty(string? name)
        => Assert.False(EntryFilters.IsLikelyUserVisible(name));

    [Theory]
    [InlineData("@shell32.dll,-12345")]
    [InlineData("@%SystemRoot%\\system32\\foo.dll,-1")]
    public void Rejects_resource_references(string name)
        => Assert.False(EntryFilters.IsLikelyUserVisible(name));

    [Theory]
    [InlineData("{B41DB860-8EE4-11D2-9906-E49FADC173CA}")]
    [InlineData("{ABC}")]
    public void Rejects_bare_clsids(string name)
        => Assert.False(EntryFilters.IsLikelyUserVisible(name));

    [Theory]
    [InlineData(@"C:\Program Files\App\thing.dll")]
    [InlineData("relative/path/here")]
    [InlineData(@"some\path")]
    public void Rejects_paths(string name)
        => Assert.False(EntryFilters.IsLikelyUserVisible(name));

    [Theory]
    [InlineData("Foo.dll")]
    [InlineData("Bar.EXE")]
    [InlineData("plugin.ocx")]
    [InlineData("icon.ico")]
    public void Rejects_filenames_with_known_extensions(string name)
        => Assert.False(EntryFilters.IsLikelyUserVisible(name));
}
