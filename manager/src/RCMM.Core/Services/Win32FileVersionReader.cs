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
        // Some CLSIDs store "<path>,<resource-id>" — drop the trailing index if it's not part of the file.
        var commaIdx = s.LastIndexOf(',');
        if (commaIdx > 0 && commaIdx > s.LastIndexOf('\\') && commaIdx > s.LastIndexOf('/'))
        {
            var head = s[..commaIdx];
            if (!File.Exists(s) && File.Exists(Environment.ExpandEnvironmentVariables(head)))
                s = head;
        }
        return Environment.ExpandEnvironmentVariables(s);
    }
}
