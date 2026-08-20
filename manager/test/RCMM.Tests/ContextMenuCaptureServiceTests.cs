using System;
using System.IO;
using System.Linq;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

[Trait("Integration", "true")]
public class ContextMenuCaptureServiceTests : System.IDisposable
{
    private readonly string _tempFile;

    public ContextMenuCaptureServiceTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"rcmm-cap-{Guid.NewGuid():N}.txt");
        File.WriteAllText(_tempFile, "");
    }

    public void Dispose()
    {
        try { File.Delete(_tempFile); } catch { }
    }

    // Not a permanent [Fact(Skip=...)]: gated so the one test that exercises the
    // real IContextMenu capture path (step 1 of the rescan pipeline) stays
    // runnable on demand instead of being dead code forever.
    [IntegrationFact("COM shell context-menu capture returns empty inside the plain dotnet test host (no Explorer STA message pump / reduced-privilege sandbox); run from an interactive full Windows process.")]
    public void CaptureAll_returns_items_for_a_temp_text_file()
    {
        var sut = new ContextMenuCaptureService();

        var captures = sut.CaptureAll(new[] { _tempFile });

        Assert.NotEmpty(captures);
        // "Open" is one of the most common verbs across all file types — we should see it.
        Assert.Contains(captures, c =>
            (c.Verb != null && c.Verb.Equals("open", StringComparison.OrdinalIgnoreCase)) ||
            c.DisplayName.Equals("Open", StringComparison.OrdinalIgnoreCase));
    }
}
