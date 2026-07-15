using System;
using System.Collections.Generic;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.ViewModels;

namespace RCMM.Tests;

public class EntryListFilterTests
{
    private static EntryRowViewModel Row(string name, bool builtIn = false, bool hidden = false)
        => new(new MenuEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = name,
            HideTargets = new List<HideTarget>(),
            IsBuiltIn = builtIn,
            IsHidden = hidden,
        });

    [Theory]
    [InlineData(OriginFilter.All, false, true)]
    [InlineData(OriginFilter.All, true, true)]
    [InlineData(OriginFilter.Apps, false, true)]
    [InlineData(OriginFilter.Apps, true, false)]
    [InlineData(OriginFilter.Windows, false, false)]
    [InlineData(OriginFilter.Windows, true, true)]
    public void Origin_filter_uses_IsBuiltIn(OriginFilter origin, bool isBuiltIn, bool expected)
    {
        var row = Row("7-Zip", builtIn: isBuiltIn);
        Assert.Equal(expected, EntryListFilter.Matches(row, origin, VisibilityFilter.All, null));
    }

    [Theory]
    [InlineData(VisibilityFilter.All, false, true)]
    [InlineData(VisibilityFilter.All, true, true)]
    [InlineData(VisibilityFilter.Visible, false, true)]
    [InlineData(VisibilityFilter.Visible, true, false)]
    [InlineData(VisibilityFilter.Hidden, false, false)]
    [InlineData(VisibilityFilter.Hidden, true, true)]
    public void Visibility_filter_uses_IsHidden(VisibilityFilter visibility, bool isHidden, bool expected)
    {
        var row = Row("7-Zip", hidden: isHidden);
        Assert.Equal(expected, EntryListFilter.Matches(row, OriginFilter.All, visibility, null));
    }

    [Theory]
    [InlineData("zip", true)]     // case-insensitive substring
    [InlineData("7-ZIP", true)]
    [InlineData("defender", false)]
    [InlineData(null, true)]      // null/empty/whitespace = no search filter
    [InlineData("", true)]
    [InlineData("   ", true)]
    public void Search_is_case_insensitive_substring_on_DisplayName(string? needle, bool expected)
    {
        var row = Row("7-Zip");
        Assert.Equal(expected, EntryListFilter.Matches(row, OriginFilter.All, VisibilityFilter.All, needle));
    }

    [Fact]
    public void Filters_compose_with_AND_semantics()
    {
        var hiddenApp = Row("Open with Code", builtIn: false, hidden: true);
        Assert.True(EntryListFilter.Matches(hiddenApp, OriginFilter.Apps, VisibilityFilter.Hidden, "code"));
        Assert.False(EntryListFilter.Matches(hiddenApp, OriginFilter.Windows, VisibilityFilter.Hidden, "code"));
        Assert.False(EntryListFilter.Matches(hiddenApp, OriginFilter.Apps, VisibilityFilter.Visible, "code"));
        Assert.False(EntryListFilter.Matches(hiddenApp, OriginFilter.Apps, VisibilityFilter.Hidden, "defender"));
    }
}
