using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

/// <summary>
/// Pins the consolidated semantics of RegistryHeuristics (review b10). Each
/// helper used to exist as several drifted private copies; these tests fix the
/// deliberately-chosen behavior so a future edit can't silently re-fork it.
/// </summary>
public class RegistryHeuristicsTests
{
    // ---- LooksLikeClsid: strict 38-char brace-wrapped GUID shape ----

    [Theory]
    [InlineData("{11111111-2222-3333-4444-555555555555}", true)]   // canonical 38 chars
    [InlineData("{0E270DAA-1BE6-48F2-AC49-00000000AAAA}", true)]
    [InlineData("{broken}", false)]                                 // brace-wrapped but malformed
    [InlineData("{ABC}", false)]
    [InlineData("11111111-2222-3333-4444-555555555555", false)]     // no braces
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeClsid_requires_full_brace_wrapped_guid_shape(string? input, bool expected)
        => Assert.Equal(expected, RegistryHeuristics.LooksLikeClsid(input));

    // ---- LooksWindowsPath: unified command/icon/MUI-spec classification ----

    [Fact]
    public void LooksWindowsPath_true_for_SystemRoot_command_with_arguments()
        => Assert.True(RegistryHeuristics.LooksWindowsPath(@"%SystemRoot%\System32\sysfoo.exe /run"));

    [Fact]
    public void LooksWindowsPath_true_for_quoted_System32_path_with_argument()
        => Assert.True(RegistryHeuristics.LooksWindowsPath(@"""C:\Windows\System32\foo.exe"" ""%1"""));

    [Fact]
    public void LooksWindowsPath_false_for_program_files_command()
        => Assert.False(RegistryHeuristics.LooksWindowsPath(@"C:\Program Files\Vendor\app.exe ""%1"""));

    [Fact]
    public void LooksWindowsPath_true_for_bare_system_dll_icon_spec()
    {
        // "shell32.dll,4" resolves via Windows' DLL search path (System32), so
        // it IS a Windows binary. The scanner's old copy said no while the
        // view-model's said yes — the same spec fed both IsBuiltIn decisions.
        Assert.True(RegistryHeuristics.LooksWindowsPath("shell32.dll,4"));
    }

    [Fact]
    public void LooksWindowsPath_true_for_mui_reference_to_bare_system_dll()
        => Assert.True(RegistryHeuristics.LooksWindowsPath("@shell32.dll,-8506"));

    [Fact]
    public void LooksWindowsPath_false_for_mui_reference_with_thirdparty_full_path()
        => Assert.False(RegistryHeuristics.LooksWindowsPath(@"@C:\Program Files\Vendor\res.dll,-1"));

    [Fact]
    public void LooksWindowsPath_false_for_forward_slash_thirdparty_path()
    {
        // The ",index" strip is guarded against '/' as well as '\' — one old
        // copy only guarded '\', so forward-slash specs parsed differently
        // depending on which pipeline stage asked.
        Assert.False(RegistryHeuristics.LooksWindowsPath("@C:/tools/foo.dll,-3"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LooksWindowsPath_false_for_empty(string? input)
        => Assert.False(RegistryHeuristics.LooksWindowsPath(input));

    // ---- ExtractExe ----

    [Theory]
    [InlineData(@"""C:\Program Files\app.exe"" ""%1""", @"C:\Program Files\app.exe")]
    [InlineData("notepad.exe %1", "notepad.exe")]
    [InlineData("cmd", "cmd")]
    public void ExtractExe_takes_quoted_or_first_token(string cmd, string expected)
        => Assert.Equal(expected, RegistryHeuristics.ExtractExe(cmd));

    // ---- IsGenericIconLibrary: filename match, quote/comma-tolerant ----

    [Theory]
    [InlineData("shell32.dll,-16762", true)]
    [InlineData(@"%SystemRoot%\System32\imageres.dll,-5302", true)]
    [InlineData(@"""C:\Windows\System32\shell32.dll"",3", true)]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\nv\nvshext.dll", false)]
    [InlineData(@"C:\Program Files\VideoLAN\VLC\vlc.exe", false)]
    [InlineData(null, false)]
    public void IsGenericIconLibrary_matches_by_filename(string? path, bool expected)
        => Assert.Equal(expected, RegistryHeuristics.IsGenericIconLibrary(path));

    // ---- VerbHandlerValueNames: the one shared field list (review finding 28:
    // one inline copy had dropped CanonicalName) ----

    [Fact]
    public void VerbHandlerValueNames_covers_every_handler_field()
        => Assert.Equal(
            new[] { "ExplorerCommandHandler", "VerbHandler", "CommandStateHandler", "CanonicalName" },
            RegistryHeuristics.VerbHandlerValueNames);
}
