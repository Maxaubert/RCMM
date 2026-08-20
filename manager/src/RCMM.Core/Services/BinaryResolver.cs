using System;
using System.Collections.Generic;
using System.IO;

namespace RCMM.Core.Services;

/// <summary>
/// Resolve a known executable's absolute path. Used by the Templates browser
/// to (a) write the registry's <c>Icon</c> value to a real .exe so the Windows
/// shell can extract the program's icon, and (b) substitute the path into
/// commands that need the absolute exe location (Git Bash isn't on PATH).
///
/// Lookup order:
///   1. Search %PATH%
///   2. Try each <c>fallbackPaths</c> entry in order (with environment-variable
///      expansion).
/// Returns null when nothing is found — callers can fall back to bare command
/// names or skip the Icon entirely.
/// </summary>
public static class BinaryResolver
{
    public static string? Find(string binaryName, IEnumerable<string>? fallbackPaths = null)
    {
        if (string.IsNullOrEmpty(binaryName)) return null;
        var fromPath = SearchPath(binaryName);
        if (fromPath != null) return fromPath;
        if (fallbackPaths == null) return null;
        foreach (var raw in fallbackPaths)
        {
            string expanded;
            try { expanded = Environment.ExpandEnvironmentVariables(raw); }
            catch { continue; }
            try { if (File.Exists(expanded)) return expanded; }
            catch { /* permission / IO error → skip */ }
        }
        return null;
    }

    private static string? SearchPath(string name)
        => SearchPath(name, Environment.GetEnvironmentVariable("PATH"));

    // internal (not private) so tests can probe a synthetic %PATH% without
    // mutating the process environment out from under parallel test classes.
    internal static string? SearchPath(string name, string? pathEnv)
    {
        if (string.IsNullOrEmpty(pathEnv)) return null;
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                // Windows accepts quoted PATH entries ("C:\Program Files\Git\cmd");
                // cmd.exe and SearchPathW resolve through them, so a user's shell
                // finds the binary while a naive Path.Combine builds "\"C:\...\"\name"
                // — a path File.Exists always rejects. Strip the surrounding quotes
                // before probing so quoted-only locations still resolve.
                var cleaned = dir.Trim().Trim('"').Trim();
                if (cleaned.Length == 0) continue;
                var full = Path.Combine(cleaned, name);
                if (File.Exists(full)) return full;
            }
            catch { /* malformed path entry → skip */ }
        }
        return null;
    }
}
