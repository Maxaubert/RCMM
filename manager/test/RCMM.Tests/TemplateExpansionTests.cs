using System;
using RCMM.Core.Models;
using RCMM.Core.Services;
using Xunit;

namespace RCMM.Tests;

/// <summary>
/// Pins TemplateExpansion.Expand — the placeholder resolution moved into Core
/// (review b10 finding 29) so the +Add flow, the template-update merge, and any
/// future headless consumer share one implementation.
/// </summary>
public class TemplateExpansionTests
{
    private static AdditionTemplates.Template Make(string command, string? icon = null,
        string? iconBinary = null)
        => new()
        {
            Name = "Test Template",
            Command = command,
            Ecosystem = "Shell",
            Scope = AdditionScope.FolderBackground,
            RunMode = RunMode.VisibleTerminal,
            Icon = icon,
            IconBinary = iconBinary,
        };

    [Fact]
    public void Expand_resolves_selfdir_to_the_process_directory()
    {
        var (command, _) = TemplateExpansion.Expand(Make(@"powershell -File ""%selfdir%\scripts\x.ps1"""));

        var selfDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath)
                      ?? AppContext.BaseDirectory.TrimEnd('\\');
        Assert.Equal($@"powershell -File ""{selfDir}\scripts\x.ps1""", command);
        Assert.DoesNotContain("%selfdir%", command);
    }

    [Fact]
    public void Expand_keeps_literal_bin_name_when_binary_is_not_found()
    {
        // The literal name (not an empty string) is deliberate: the user sees
        // exactly which binary is missing when the command runs.
        var t = Make(@"""%bin%"" ""%V""", iconBinary: "definitely-not-installed-xyz.exe");
        var (command, icon) = TemplateExpansion.Expand(t);

        Assert.Equal(@"""definitely-not-installed-xyz.exe"" ""%V""", command);
        Assert.Null(icon);
    }

    [Fact]
    public void Expand_prefers_explicit_lib_icon_over_resolved_binary()
    {
        var t = Make("cmd /k echo hi", icon: "lib:claude", iconBinary: "definitely-not-installed-xyz.exe");
        var (_, icon) = TemplateExpansion.Expand(t);

        Assert.Equal("lib:claude", icon);
    }

    [Fact]
    public void Expand_without_placeholders_returns_command_unchanged()
    {
        var (command, icon) = TemplateExpansion.Expand(Make("cmd /k echo hi"));
        Assert.Equal("cmd /k echo hi", command);
        Assert.Null(icon);
    }
}
