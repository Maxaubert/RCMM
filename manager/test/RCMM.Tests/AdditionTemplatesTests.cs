using Xunit;
using RCMM.Core.Models;
using RCMM.Core.Services;

namespace RCMM.Tests;

public class AdditionTemplatesTests
{
    [Fact]
    public void All_thirteen_templates_present()
    {
        var templates = AdditionTemplates.All;
        Assert.Equal(13, templates.Count);
    }

    [Fact]
    public void All_templates_target_FolderBackground_with_VisibleTerminal()
    {
        foreach (var t in AdditionTemplates.All)
        {
            Assert.Equal(AdditionScope.FolderBackground, t.Scope);
            Assert.Equal(RunMode.VisibleTerminal, t.RunMode);
            Assert.Equal("%V", t.WorkingDir);
        }
    }

    [Fact]
    public void Commands_are_bare_no_cmd_wrapper()
    {
        foreach (var t in AdditionTemplates.All)
            Assert.DoesNotContain("cmd /k", t.Command);
    }

    [Theory]
    [InlineData("npm run dev")]
    [InlineData("git pull")]
    [InlineData("dotnet build")]
    [InlineData("cargo run")]
    [InlineData("docker compose up")]
    public void Specific_template_exists(string expectedCommand)
    {
        Assert.Contains(AdditionTemplates.All, t => t.Command == expectedCommand);
    }
}
