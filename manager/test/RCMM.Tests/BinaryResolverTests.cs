using System;
using System.IO;
using Xunit;
using RCMM.Core.Services;

namespace RCMM.Tests;

public class BinaryResolverTests
{
    private static string MakeDirWithExe(out string exe)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rcmm-binres-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        exe = Path.Combine(dir, "probe-tool.exe");
        File.WriteAllText(exe, "stub");
        return dir;
    }

    [Fact]
    public void SearchPath_resolves_through_a_quoted_PATH_entry()
    {
        // Windows accepts quoted PATH entries ("C:\Program Files\...") and cmd.exe
        // resolves through them, so RCMM must too — a naive Path.Combine produced
        // "\"C:\...\"\probe-tool.exe" which File.Exists always rejected.
        var dir = MakeDirWithExe(out var exe);
        try
        {
            var found = BinaryResolver.SearchPath("probe-tool.exe", $"C:\\NoSuchDir;\"{dir}\"");
            Assert.Equal(exe, found);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SearchPath_still_resolves_plain_entries_and_skips_empties()
    {
        var dir = MakeDirWithExe(out var exe);
        try
        {
            Assert.Equal(exe, BinaryResolver.SearchPath("probe-tool.exe", $";;{dir};"));
            Assert.Null(BinaryResolver.SearchPath("no-such-binary.exe", dir));
            Assert.Null(BinaryResolver.SearchPath("probe-tool.exe", "\"\";\" \""));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
