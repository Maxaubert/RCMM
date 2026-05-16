using System.IO;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class IconMaterializerTests
{
    [Fact]
    public void Pass_through_non_library_values()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rcmm-icons-test-" + System.Guid.NewGuid().ToString("N"));
        var m = new IconMaterializer(dir);

        Assert.Null(m.Materialize(null));
        Assert.Equal("", m.Materialize(""));
        Assert.Equal(@"C:\Windows\System32\shell32.dll,4", m.Materialize(@"C:\Windows\System32\shell32.dll,4"));
        Assert.False(Directory.Exists(dir), "no library refs so no dir should be created");
    }

    [Fact]
    public void Renders_every_library_icon_without_throwing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rcmm-icons-test-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var m = new IconMaterializer(dir);
            foreach (var name in IconLibrary.AllNames)
            {
                var path = m.Materialize(IconLibrary.MakeLibValue(name));
                Assert.NotNull(path);
                Assert.True(File.Exists(path!), $"icon {name} missing at {path}");
                var size = new FileInfo(path!).Length;
                Assert.True(size > 200, $"icon {name} suspiciously small: {size} bytes");
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Returns_cached_path_on_second_call()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rcmm-icons-test-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var m = new IconMaterializer(dir);
            var libVal = IconLibrary.MakeLibValue("terminal");
            var p1 = m.Materialize(libVal);
            var t1 = File.GetLastWriteTimeUtc(p1!);
            var p2 = m.Materialize(libVal);
            var t2 = File.GetLastWriteTimeUtc(p2!);
            Assert.Equal(p1, p2);
            Assert.Equal(t1, t2);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
