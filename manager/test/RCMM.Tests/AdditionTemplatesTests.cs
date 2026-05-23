using System.Linq;
using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Tests;

public class AdditionTemplatesTests
{
    [Fact]
    public void All_templates_target_folder_background()
    {
        // Every template is a "do something in this folder" command, so they
        // all live under Directory\Background. WorkingDir is %V for the same
        // reason (the folder you right-clicked).
        foreach (var t in AdditionTemplates.All)
        {
            // Smart-action entries (Convert) target a clicked File, not the folder.
            if (t.Scope == AdditionScope.File) continue;
            Assert.Equal(AdditionScope.FolderBackground, t.Scope);
            Assert.Equal("%V", t.WorkingDir);
        }
    }

    [Fact]
    public void Commands_are_bare_no_cmd_wrapper()
    {
        foreach (var t in AdditionTemplates.All)
            Assert.DoesNotContain("cmd /k", t.Command);
    }

    [Fact]
    public void Project_and_shell_sections_use_background_runmode_others_use_terminal()
    {
        foreach (var t in AdditionTemplates.All)
        {
            // Smart actions (Files) launch RCMM directly — Background, no binary.
            if (t.Ecosystem == "Files") continue;
            if (t.Ecosystem == "Open project" || t.Ecosystem == "Shell")
            {
                // Editors and shells launch their own window; wrapping in
                // cmd /k would spawn an unnecessary intermediate cmd window.
                Assert.Equal(RunMode.Background, t.RunMode);
                Assert.NotNull(t.IconBinary);
            }
            else
            {
                Assert.Equal(RunMode.VisibleTerminal, t.RunMode);
                Assert.Null(t.IconBinary);
            }
        }
    }

    [Fact]
    public void Section_order_is_popular_first()
    {
        // GroupBy preserves first-appearance order, so the relative order of
        // sections in the catalogue is also their order in the Templates UI.
        var sectionsInOrder = AdditionTemplates.All
            .Select(t => t.Ecosystem)
            .Distinct()
            .ToList();
        Assert.Equal(
            new[] { "Git", "Node", "Open project", "Shell", "Python", ".NET", "Rust", "Go", "Bun", "pnpm", "uv", "GitHub CLI", "Files" },
            sectionsInOrder);
    }

    [Fact]
    public void Open_project_entries_named_consistently()
    {
        var project = AdditionTemplates.All.Where(t => t.Ecosystem == "Open project").ToList();
        Assert.NotEmpty(project);
        foreach (var t in project)
            Assert.StartsWith("Open project in ", t.Name);
    }

    [Theory]
    [InlineData("git pull")]
    [InlineData("git push")]
    [InlineData("git stash pop")]
    [InlineData("npm run dev")]
    [InlineData("npm run build")]
    [InlineData("pytest")]
    [InlineData("dotnet test")]
    [InlineData("cargo test")]
    [InlineData("go build")]
    [InlineData("go run .")]
    [InlineData("bun install")]
    [InlineData("pnpm install")]
    [InlineData("uv sync")]
    [InlineData("gh pr create --web")]
    public void Specific_command_template_exists(string expectedCommand)
    {
        Assert.Contains(AdditionTemplates.All, t => t.Command == expectedCommand);
    }

    [Theory]
    [InlineData("Open project in VS Code",  "Open project", "Code.exe")]
    [InlineData("Open project in Cursor",   "Open project", "Cursor.exe")]
    [InlineData("Open project in Windsurf", "Open project", "Windsurf.exe")]
    [InlineData("PowerShell here",          "Shell",        "powershell.exe")]
    [InlineData("Command Prompt here",      "Shell",        "cmd.exe")]
    [InlineData("Git Bash here",            "Shell",        "git-bash.exe")]
    [InlineData("WSL here",                 "Shell",        "wsl.exe")]
    [InlineData("Windows Terminal here",    "Shell",        "wt.exe")]
    [InlineData("Open Claude here",         "Shell",        "wt.exe")]
    [InlineData("Open Codex here",          "Shell",        "wt.exe")]
    public void Binary_template_metadata(string name, string ecosystem, string expectedBinary)
    {
        var t = AdditionTemplates.All.SingleOrDefault(x => x.Name == name);
        Assert.NotNull(t);
        Assert.Equal(ecosystem, t!.Ecosystem);
        Assert.Equal(expectedBinary, t.IconBinary);
    }

    [Fact]
    public void Convert_smart_action_template_exists()
    {
        var t = AdditionTemplates.All.SingleOrDefault(x => x.Ecosystem == "Files");
        Assert.NotNull(t);
        Assert.Equal("Convert / Change format", t!.Name);
        Assert.Equal(AdditionScope.File, t.Scope);
        Assert.Equal(RunMode.Background, t.RunMode);
        Assert.Contains("%selfdir%", t.Command);
        Assert.Contains("rcmm-convert.ps1", t.Command);
        Assert.Contains("%1", t.Command);
    }

    [Fact]
    public void Docker_templates_removed()
    {
        // We dropped the Docker section per user preference; make sure no
        // stray docker entry survives.
        Assert.DoesNotContain(AdditionTemplates.All, t => t.Ecosystem == "Docker");
        Assert.DoesNotContain(AdditionTemplates.All, t => t.Command.StartsWith("docker"));
    }
}
