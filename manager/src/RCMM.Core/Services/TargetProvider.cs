using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RCMM.Core.Services;

public sealed class TargetProvider
{
    private static readonly string[] SampleExtensions =
        { ".txt", ".png", ".mp4", ".mp3", ".pdf", ".zip", ".exe", ".lnk" };

    private readonly string _root;

    public TargetProvider() : this(DefaultRoot()) { }
    public TargetProvider(string root) { _root = root; }

    public static string DefaultRoot()
        => Path.Combine(Path.GetTempPath(), "rcmm-capture");

    public IReadOnlyList<string> GetTargets()
    {
        Directory.CreateDirectory(_root);

        var result = new List<string> { _root };

        var firstDrive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady)?.RootDirectory.FullName;
        if (firstDrive != null) result.Add(firstDrive);

        foreach (var ext in SampleExtensions)
        {
            var path = Path.Combine(_root, "sample" + ext);
            try
            {
                // Write at least 1 byte. Several shell extensions skip zero-byte files
                // (they treat them as placeholders) so an empty file misses entries
                // those handlers would otherwise contribute.
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    File.WriteAllBytes(path, new byte[] { 0x00 });
            }
            catch { continue; }
            result.Add(path);
        }

        return result;
    }

    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }
}
