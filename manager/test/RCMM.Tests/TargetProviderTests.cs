using System.IO;
using System.Linq;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

public class TargetProviderTests : System.IDisposable
{
    private readonly string _root;
    private readonly TargetProvider _sut;

    public TargetProviderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rcmm-test-{System.Guid.NewGuid():N}");
        _sut = new TargetProvider(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void GetTargets_creates_temp_files_for_each_sample_extension()
    {
        var targets = _sut.GetTargets().ToList();

        Assert.Contains(targets, p => p.EndsWith(".txt"));
        Assert.Contains(targets, p => p.EndsWith(".png"));
        Assert.Contains(targets, p => p.EndsWith(".mp4"));
        Assert.Contains(targets, p => p.EndsWith(".mp3"));
        Assert.Contains(targets, p => p.EndsWith(".pdf"));
        Assert.Contains(targets, p => p.EndsWith(".zip"));
        Assert.Contains(targets, p => p.EndsWith(".exe"));
        Assert.Contains(targets, p => p.EndsWith(".lnk"));
        foreach (var t in targets.Where(p => Path.HasExtension(p)))
            Assert.True(File.Exists(t), $"expected file to exist: {t}");
    }

    [Fact]
    public void GetTargets_includes_folder_and_drive()
    {
        var targets = _sut.GetTargets().ToList();
        Assert.Contains(_root, targets);
        Assert.Contains(targets, p => p.Length == 3 && p[1] == ':' && p[2] == '\\');
    }

    [Fact]
    public void GetTargets_is_idempotent()
    {
        var first = _sut.GetTargets().ToList();
        var second = _sut.GetTargets().ToList();
        Assert.Equal(first, second);
    }
}
