using System;
using System.Diagnostics;
using System.IO;

namespace RCMM.Core.Services;

public sealed class Win32FileVersionReader : IFileVersionReader
{
    public FileVersion Read(string path)
    {
        try
        {
            var expanded = NormalizePath(path);
            if (expanded is null || !File.Exists(expanded))
                return new FileVersion(null, null, null);

            var info = FileVersionInfo.GetVersionInfo(expanded);
            return new FileVersion(info.FileDescription, info.CompanyName, info.ProductName);
        }
        catch
        {
            return new FileVersion(null, null, null);
        }
    }

    private static string? NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.StartsWith('"') && s.EndsWith('"') && s.Length >= 2) s = s[1..^1];
        // "<path>,<resource-id>" — drop the trailing index when the head names a real file.
        var commaIdx = s.LastIndexOf(',');
        if (commaIdx > 0 && commaIdx > s.LastIndexOf('\\') && commaIdx > s.LastIndexOf('/'))
        {
            var head = s[..commaIdx].Trim();
            if (!File.Exists(s) && File.Exists(Environment.ExpandEnvironmentVariables(head)))
                s = head;
            else if (!File.Exists(s)) s = head;  // also strip when neither variant exists yet — the System32 fallback below will try
        }
        s = Environment.ExpandEnvironmentVariables(s);
        // Bare filename (no directory) — fall back to System32 the way Windows'
        // DLL search path would. Lets us read shell32.dll / imageres.dll info
        // from a CommandStore Icon hint like "shell32.dll,-16762".
        if (!File.Exists(s) && !s.Contains('\\') && !s.Contains('/'))
        {
            var sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), s);
            if (File.Exists(sys32)) s = sys32;
        }
        return s;
    }
}
