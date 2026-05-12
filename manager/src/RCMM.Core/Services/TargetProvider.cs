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
            if (!File.Exists(path))
            {
                try { using (File.Create(path)) { } }
                catch { continue; }
            }
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
