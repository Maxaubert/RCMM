using System.Linq;
using Xunit;
using RCMM.Core.Services;

namespace RCMM.Tests;

public class IconLibraryTests
{
    [Fact]
    public void Categories_have_at_least_one_icon_each()
    {
        foreach (var cat in IconLibrary.Categories)
            Assert.NotEmpty(cat.Icons);
    }

    [Fact]
    public void Every_named_icon_resolves_to_path_data()
    {
        foreach (var name in IconLibrary.AllNames)
        {
            var data = IconLibrary.TryGetPathData(name);
            Assert.False(string.IsNullOrWhiteSpace(data), $"icon {name} has no resolved path data");
        }
    }

    [Fact]
    public void IsLibraryName_recognises_lib_prefix()
    {
        Assert.True(IconLibrary.IsLibraryName("lib:terminal"));
        Assert.False(IconLibrary.IsLibraryName("C:\\Windows\\System32\\imageres.dll,42"));
        Assert.False(IconLibrary.IsLibraryName(null));
        Assert.False(IconLibrary.IsLibraryName(""));
    }

    [Fact]
    public void StripPrefix_returns_name_only_for_lib_values()
    {
        Assert.Equal("folder", IconLibrary.StripPrefix("lib:folder"));
        Assert.Null(IconLibrary.StripPrefix("raw\\path"));
        Assert.Null(IconLibrary.StripPrefix(null));
    }

    [Fact]
    public void SvgFragmentToPathData_converts_line_to_M_L_pair()
    {
        var data = IconLibrary.SvgFragmentToPathData("<line x1='1' y1='2' x2='3' y2='4'/>");
        Assert.Equal("M 1 2 L 3 4", data);
    }

    [Fact]
    public void SvgFragmentToPathData_converts_rect_no_radius_to_rectangular_subpath()
    {
        var data = IconLibrary.SvgFragmentToPathData("<rect x='0' y='0' width='10' height='5'/>");
        Assert.Equal("M 0 0 H 10 V 5 H 0 Z", data);
    }

    [Fact]
    public void SvgFragmentToPathData_converts_rect_with_radius_to_arc_corners()
    {
        var data = IconLibrary.SvgFragmentToPathData("<rect x='0' y='0' width='10' height='10' rx='2'/>");
        Assert.Contains("A 2 2 0 0 1", data); // four arc corners
        Assert.Equal(4, data.Split("A 2 2 0 0 1", System.StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void SvgFragmentToPathData_converts_polyline_to_M_L_chain()
    {
        var data = IconLibrary.SvgFragmentToPathData("<polyline points='4 17 10 11 4 5'/>");
        Assert.Equal("M 4 17 L 10 11 L 4 5", data);
    }

    [Fact]
    public void SvgFragmentToPathData_passes_path_d_through()
    {
        var data = IconLibrary.SvgFragmentToPathData("<path d='M 0 0 L 10 10'/>");
        Assert.Equal("M 0 0 L 10 10", data);
    }

    [Fact]
    public void SvgFragmentToPathData_combines_multiple_shapes_separated_by_spaces()
    {
        var data = IconLibrary.SvgFragmentToPathData(
            "<line x1='0' y1='0' x2='1' y2='1'/><line x1='2' y1='2' x2='3' y2='3'/>");
        Assert.Equal("M 0 0 L 1 1 M 2 2 L 3 3", data);
    }
}
