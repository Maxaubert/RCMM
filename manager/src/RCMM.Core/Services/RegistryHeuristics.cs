using System;
using System.IO;

namespace RCMM.Core.Services;

/// <summary>
/// Shared string heuristics over registry values. Each of these used to exist
/// as several privately-drifted copies (MainViewModel, the scanners, the
/// indexes, the invoker); any fix applied to one copy silently missed the
/// others, so the same value could be classified differently by different
/// pipeline stages. One implementation, one set of semantics, pinned by
/// RegistryHeuristicsTests.
/// </summary>
internal static class RegistryHeuristics
{
    /// <summary>
    /// Every registry value name that can point a classic verb at a handler
    /// CLSID. CommandStore entries route the verb through any of these
    /// (Windows.share → ExplorerCommandHandler; Windows.copyaspath →
    /// VerbHandler), so callers enumerating "fields that may hold a handler
    /// CLSID" must check all of them — probing a wrong CLSID is harmless,
    /// missing the right one is the bug we hit on copyaspath. One drifted
    /// inline copy used to omit CanonicalName.
    /// </summary>
    internal static readonly string[] VerbHandlerValueNames =
    {
        "ExplorerCommandHandler",
        "VerbHandler",
        "CommandStateHandler",
        "CanonicalName"
    };

    /// <summary>
    /// True when the string is shaped like a registry CLSID. Strict semantics
    /// chosen deliberately: a canonical brace-wrapped GUID is exactly 38 chars
    /// ({8-4-4-4-12}), so shorter brace strings ("{broken}") are malformed
    /// registrations — every downstream consumer Guid-parses before real COM
    /// use and would reject them anyway, so gating early keeps the whole
    /// pipeline consistent instead of half of it treating garbage as a CLSID.
    /// (Three copies used to accept any brace-wrapped string.)
    /// </summary>
    internal static bool LooksLikeClsid(string? s)
        => !string.IsNullOrEmpty(s) && s.Length >= 38 && s.StartsWith('{') && s.EndsWith('}');

    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();

    /// <summary>
    /// True when a command line / icon spec / MUI reference points at a binary
    /// that ships with Windows. Union of the two previously drifted copies,
    /// each piece kept deliberately:
    ///   • '@' MUI prefix stripping ("@shell32.dll,-8506") — MUIVerb values are
    ///     legitimate inputs (was only in the scanner copy);
    ///   • unquoted specs truncate at the first space so a command's arguments
    ///     ("%1", "/run") don't defeat the path checks (scanner copy);
    ///   • the trailing ",index" strip guards against forward-slash paths as
    ///     well as backslash ones (was only in the view-model copy);
    ///   • bare filenames ("shell32.dll") resolve via Windows' DLL search path,
    ///     i.e. System32, so an existing System32 file counts as a Windows
    ///     binary (was only in the view-model copy — the scanner classified
    ///     the very same spec as not-Windows).
    /// </summary>
    internal static bool LooksWindowsPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try
        {
            var s = StripSpecCore(raw!, truncateAtSpace: true);
            if (WindowsDir.Length > 0 && s.StartsWith(WindowsDir + "\\")) return true;
            if (s.Contains(@"\windows\system32\") || s.Contains(@"\windows\syswow64\")) return true;
            if (!s.Contains('\\') && !s.Contains('/')
                && (s.EndsWith(".dll") || s.EndsWith(".exe") || s.EndsWith(".mui")))
            {
                var sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), s);
                if (File.Exists(sys32)) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// The executable token of a shell command line: the quoted prefix when the
    /// command is quoted ("C:\path\app.exe" "%1"), else everything before the
    /// first space. Case and env vars are preserved — callers hand the result
    /// to file-version probing, which wants the original string.
    /// </summary>
    internal static string? ExtractExe(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            if (end > 1) return cmd[1..end];
        }
        var space = cmd.IndexOf(' ');
        return space > 0 ? cmd[..space] : cmd;
    }

    /// <summary>
    /// True for the handful of generic-icon Windows libraries whose index-0
    /// resource is a placeholder (the shell uses an HBITMAP the handler emits
    /// at runtime, not a resource we can read here). Used as a last-mile filter
    /// in icon resolution so we don't return a generic key/folder/document icon.
    ///
    /// Match is by *filename* not "path is anywhere under System32" — that
    /// earlier rule misfiled third-party DLLs like NVIDIA's
    /// System32\DriverStore\...\nvshext.dll as generic and stripped a perfectly
    /// good icon source, leaving "NVIDIA Control Panel" with no icon in the UI.
    /// </summary>
    internal static bool IsGenericIconLibrary(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var fileName = Path.GetFileName(StripSpecCore(path!, truncateAtSpace: false).Trim());
            return fileName is "shell32.dll" or "imageres.dll" or "windows.storage.dll"
                            or "ntshrui.dll" or "ddores.dll" or "explorer.exe";
        }
        catch { return false; }
    }

    /// <summary>
    /// The canonical quote/comma icon-spec parse, shared by the heuristics
    /// above (five slightly-different inline copies once existed). Reduces a
    /// spec to its lower-cased path: expands env vars, drops an MUI '@'
    /// prefix, takes the quoted token (or, when asked, the first
    /// space-delimited token — icon specs keep their spaces, command lines
    /// don't), and strips a trailing ",index" only when the comma comes after
    /// the last path separator ('\' or '/') so commas inside directories
    /// survive.
    /// </summary>
    private static string StripSpecCore(string raw, bool truncateAtSpace)
    {
        var s = Environment.ExpandEnvironmentVariables(raw).Trim().ToLowerInvariant();
        if (s.StartsWith('@')) s = s[1..];
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end > 1) s = s[1..end];
        }
        else if (truncateAtSpace)
        {
            var space = s.IndexOf(' ');
            if (space > 0) s = s[..space];
        }
        var comma = s.LastIndexOf(',');
        if (comma > 0 && comma > s.LastIndexOf('\\') && comma > s.LastIndexOf('/'))
            s = s[..comma].Trim();
        return s;
    }
}
