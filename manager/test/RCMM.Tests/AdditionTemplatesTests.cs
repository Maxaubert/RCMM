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
            // Smart-action entries target a clicked File / Folder / any object,
            // not the folder background (Change format / Upscale / Remove background).
            if (t.Scope == AdditionScope.File) continue;
            if (t.Scope == AdditionScope.Folder) continue;
            if (t.Scope == AdditionScope.AllFilesystemObjects) continue;
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
    [InlineData("Open Gemini here",         "Shell",        "wt.exe")]
    [InlineData("Open Aider here",          "Shell",        "wt.exe")]
    [InlineData("Open Claude (resume)",     "Shell",        "wt.exe")]
    [InlineData("Open admin Terminal here", "Shell",        "wt.exe")]
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
        var t = AdditionTemplates.All.SingleOrDefault(x => x.Name == "Change format");
        Assert.NotNull(t);
        Assert.Equal("Files", t!.Ecosystem);
        Assert.Equal(AdditionScope.File, t.Scope);
        Assert.Equal(RunMode.Background, t.RunMode);
        Assert.Contains("%selfdir%", t.Command);
        Assert.Contains("rcmm-convert.ps1", t.Command);
        Assert.Contains("%1", t.Command);
        Assert.Contains("heic", t.FileTypes!);   // iPhone photos route to the image pipeline
        Assert.Contains("svg", t.FileTypes!);     // vector input -> raster targets
    }

    [Fact]
    public void Compress_smart_action_template_exists()
    {
        var t = AdditionTemplates.All.SingleOrDefault(x => x.Name == "Compress");
        Assert.NotNull(t);
        Assert.Equal("Files", t!.Ecosystem);
        Assert.Equal(AdditionScope.File, t.Scope);
        Assert.Equal(RunMode.Background, t.RunMode);
        Assert.Contains("%selfdir%", t.Command);
        Assert.Contains("rcmm-compress.ps1", t.Command);
        Assert.Contains("%1", t.Command);
        Assert.Equal("lib:shrink", t.Icon);
        Assert.Contains("mp4", t.FileTypes!);   // video — ffmpeg pipeline
        Assert.Contains("png", t.FileTypes!);   // image — CaesiumCLT pipeline
    }

    [Fact]
    public void Upscale_smart_action_template_exists()
    {
        var t = AdditionTemplates.All.SingleOrDefault(x => x.Name == "Upscale");
        Assert.NotNull(t);
        Assert.Equal("Files", t!.Ecosystem);
        Assert.Equal(AdditionScope.File, t.Scope);
        Assert.Equal(RunMode.Background, t.RunMode);
        Assert.Contains("%selfdir%", t.Command);
        Assert.Contains("rcmm-upscale.ps1", t.Command);
        Assert.Contains("%1", t.Command);
        Assert.Equal("lib:arrow-big-up-dash", t.Icon);
    }

    [Theory]
    [InlineData("Change format",    "mp4")]
    [InlineData("Compress",         "mp4")]
    [InlineData("Upscale",          "png")]
    [InlineData("Remove background", "png")]
    public void Media_smart_actions_are_file_type_scoped(string name, string sampleExt)
    {
        // Scoped to relevant extensions so they appear only on those file types,
        // not on every file (the catch-all "*").
        var t = AdditionTemplates.All.SingleOrDefault(x => x.Name == name);
        Assert.NotNull(t);
        Assert.Equal(AdditionScope.File, t!.Scope);
        Assert.NotNull(t.FileTypes);
        Assert.NotEmpty(t.FileTypes!);
        Assert.Contains(sampleExt, t.FileTypes!);
    }

    [Fact]
    public void Remove_background_has_no_separate_folder_entry()
    {
        // One image-scoped File entry; the folder-batch variant was dropped.
        Assert.DoesNotContain(AdditionTemplates.All, x => x.Name == "Remove backgrounds");
    }

    [Fact]
    public void Upscale_folder_template_exists()
    {
        // Folder right-click variant — same script, Folder scope, batch-upscales
        // every image inside.
        var t = AdditionTemplates.All.SingleOrDefault(x => x.Name == "Upscale images");
        Assert.NotNull(t);
        Assert.Equal("Files", t!.Ecosystem);
        Assert.Equal(AdditionScope.Folder, t.Scope);
        Assert.Equal(RunMode.Background, t.RunMode);
        Assert.Contains("rcmm-upscale.ps1", t.Command);
        Assert.Contains("%1", t.Command);
    }

    [Theory]
    [InlineData("Copy SHA-256",   "sha256",  "lib:hash")]
    [InlineData("Unblock file",   "unblock", "lib:shield-check")]
    [InlineData("Take ownership", "takeown", "lib:key")]
    public void Action_file_templates_call_rcmm_action(string name, string action, string expectedIcon)
    {
        var t = AdditionTemplates.All.SingleOrDefault(x => x.Name == name);
        Assert.NotNull(t);
        Assert.Equal("Files", t!.Ecosystem);
        Assert.Equal(AdditionScope.File, t.Scope);
        Assert.Equal(RunMode.Background, t.RunMode);
        Assert.Contains("rcmm-action.ps1", t.Command);
        Assert.Contains("-Action " + action, t.Command);
        Assert.Contains("%1", t.Command);
        Assert.Equal(expectedIcon, t.Icon);   // guard: these had no icon before
    }

    [Fact]
    public void Admin_terminal_template_exists()
    {
        var t = AdditionTemplates.All.SingleOrDefault(x => x.Name == "Open admin Terminal here");
        Assert.NotNull(t);
        Assert.Equal("Shell", t!.Ecosystem);
        Assert.Equal(AdditionScope.FolderBackground, t.Scope);
        Assert.Contains("rcmm-action.ps1", t.Command);
        Assert.Contains("-Action adminterm", t.Command);
        Assert.Contains("%V", t.Command);
    }

    [Fact]
    public void Featured_lineup_all_resolve_in_order()
    {
        // The ★ Featured chip is curated by name; every name must match a real
        // template (so a rename like Convert→Change format can't silently drop
        // one), and the lineup is owner-fixed in this order.
        Assert.Equal(
            new[] { "Upscale", "Compress", "Change format", "Open Claude here", "Open Codex here" },
            AdditionTemplates.Featured.ToArray());
        foreach (var name in AdditionTemplates.Featured)
            Assert.Contains(AdditionTemplates.All, t => t.Name == name);
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
