using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace RCMM.Tests;

// rcmm-action.ps1 hands arguments to ELEVATED shells (wt.exe / powershell) and
// re-launches itself elevated for takeown, so its quoting and its refusal to
// elevate from a user-writable folder are security behavior worth pinning.
// Rather than re-implementing the functions in C# (which would drift), these
// tests parse the shipped script's AST in a child Windows PowerShell 5.1
// process — the same host the registry verbs use — extract the function under
// test, and drive it with hostile inputs. No registry, no %LOCALAPPDATA%.
public sealed class RcmmActionScriptTests
{
    private static string ScriptPath => Path.Combine(AppContext.BaseDirectory, "rcmm-action.ps1");

    private static string RunPs(string driver)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "
                        + Convert.ToBase64String(Encoding.Unicode.GetBytes(driver)),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(60_000), "powershell.exe did not exit within 60s");
        Assert.True(p.ExitCode == 0, $"powershell exited {p.ExitCode}: {stderr}\n{stdout}");
        return stdout;
    }

    // Dot-sources just the named function out of the real script file, so the
    // driver never executes the script's action switch (which pops balloons
    // and can prompt UAC).
    private static string ExtractFunction(string name) => $@"
$ErrorActionPreference = 'Stop'
$t = $null; $e = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile('{ScriptPath.Replace("'", "''")}', [ref]$t, [ref]$e)
$fn = $ast.Find({{ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq '{name}' }}, $true)
if ($null -eq $fn) {{ throw 'function {name} not found in rcmm-action.ps1' }}
. ([scriptblock]::Create($fn.Extent.Text))
";

    [Fact]
    public void QuoteArg_quotes_metacharacters_and_leaves_plain_paths_alone()
    {
        // The ';' case is the load-bearing one: wt.exe splits subcommands on an
        // unquoted ';' even with no whitespace around it, and the adminterm verb
        // passes Quote-Arg's result to an ELEVATED wt. The rest pin the rule
        // "anything outside [A-Za-z0-9_-.:\/] gets wrapped" plus the pre-existing
        // trailing-backslash and empty-string handling.
        var cases = new (string Input, string Expected)[]
        {
            (@"C:\Windows",             @"C:\Windows"),                 // plain path stays identical
            (@"C:\",                    @"C:\"),                        // drive root stays identical
            (@"C:\Program Files\x",     "\"C:\\Program Files\\x\""),    // whitespace (old behavior kept)
            (@"C:\dl\proj;split-pane",  "\"C:\\dl\\proj;split-pane\""), // the spaceless wt ';' injection
            (@"C:\evil&calc",           "\"C:\\evil&calc\""),
            (@"C:\a|b",                 "\"C:\\a|b\""),
            (@"C:\a(b)",                "\"C:\\a(b)\""),
            (@"C:\a$b",                 "\"C:\\a$b\""),
            (@"C:\a`b",                 "\"C:\\a`b\""),
            (@"C:\a,b",                 "\"C:\\a,b\""),
            (@"C:\a b\",                "\"C:\\a b\\\\\""),             // trailing backslash doubled
            ("",                        "\"\""),
        };

        var driver = new StringBuilder(ExtractFunction("Quote-Arg"));
        foreach (var c in cases)
            driver.AppendLine($"Write-Output ('OUT<' + (Quote-Arg '{c.Input.Replace("'", "''")}') + '>')");

        var lines = RunPs(driver.ToString())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("OUT<", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(cases.Length, lines.Length);
        for (int i = 0; i < cases.Length; i++)
            Assert.Equal("OUT<" + cases[i].Expected + ">", lines[i]);
    }

    [Fact]
    public void Writability_probe_detects_writable_dir_and_cleans_up()
    {
        string writable = Path.Combine(Path.GetTempPath(), "rcmm-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(writable);
        try
        {
            string missing = Path.Combine(writable, "missing-sub");
            string driver = ExtractFunction("Test-ScriptDirUserWritable") + $@"
Write-Output ('W<' + (Test-ScriptDirUserWritable -Dir '{writable.Replace("'", "''")}') + '>')
Write-Output ('M<' + (Test-ScriptDirUserWritable -Dir '{missing.Replace("'", "''")}') + '>')
";
            string output = RunPs(driver);
            Assert.Contains("W<True>", output);   // user-writable dir detected
            Assert.Contains("M<False>", output);  // unwritable (missing) dir does not false-positive
            Assert.Empty(Directory.GetFiles(writable)); // the probe file never survives
        }
        finally
        {
            Directory.Delete(writable, recursive: true);
        }
    }

    [Fact]
    public void Takeown_branch_guards_elevation_behind_the_writability_probe()
    {
        // Structural pin: the refusal must sit between the takeown case label
        // and the -Verb RunAs re-launch, or a refactor could silently drop the
        // defense while everything still "works" from Program Files.
        string text = File.ReadAllText(ScriptPath);
        int takeown = text.IndexOf("'takeown' {", StringComparison.Ordinal);
        Assert.True(takeown >= 0, "takeown switch case not found");
        int guard = text.IndexOf("Test-ScriptDirUserWritable", takeown, StringComparison.Ordinal);
        int elevate = text.IndexOf("-Verb RunAs", takeown, StringComparison.Ordinal);
        Assert.True(guard >= 0, "writability guard not called in the takeown branch");
        Assert.True(elevate >= 0, "takeown UAC re-launch not found");
        Assert.True(guard < elevate, "the writable-dir refusal must run before the UAC re-launch");
    }
}
