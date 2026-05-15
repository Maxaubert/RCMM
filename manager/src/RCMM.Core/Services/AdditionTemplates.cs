using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

/// <summary>
/// Static catalogue of predefined entry templates shipped with RCMM. Not persisted —
/// when the user "Adds" a template, a fresh AdditionEntry is created with the
/// template's defaults; the link is severed and the new entry is fully editable.
///
/// All v1 templates target FolderBackground (right-click empty space in a folder) since
/// they're all "run in this directory" commands.
/// </summary>
public static class AdditionTemplates
{
    public sealed record Template
    {
        public required string Name { get; init; }
        public required string Command { get; init; }
        public required string Ecosystem { get; init; }    // grouping label in Templates browser
        public required string AppliesWhen { get; init; }  // informational, not enforced
        public required AdditionScope Scope { get; init; }
        public required RunMode RunMode { get; init; }
        public string WorkingDir { get; init; } = "%V";
    }

    public static IReadOnlyList<Template> All { get; } = new List<Template>
    {
        Make("npm run dev",                   "npm run dev",                     "Node",   "package.json"),
        Make("npm install",                   "npm install",                     "Node",   "package.json"),
        Make("npm test",                      "npm test",                        "Node",   "package.json"),
        Make("git pull",                      "git pull",                        "Git",    ".git/"),
        Make("git status",                    "git status",                      "Git",    ".git/"),
        Make("git fetch --all",               "git fetch --all",                 "Git",    ".git/"),
        Make("dotnet build",                  "dotnet build",                    ".NET",   "*.csproj or *.sln"),
        Make("dotnet run",                    "dotnet run",                      ".NET",   "*.csproj"),
        Make("python -m venv .venv",          "python -m venv .venv",            "Python", "pyproject.toml / requirements.txt"),
        Make("pip install -r requirements",   "pip install -r requirements.txt", "Python", "requirements.txt"),
        Make("cargo run",                     "cargo run",                       "Rust",   "Cargo.toml"),
        Make("go run .",                      "go run .",                        "Go",     "go.mod"),
        Make("docker compose up",             "docker compose up",               "Docker", "compose.yaml"),
    };

    private static Template Make(string name, string command, string ecosystem, string when)
        => new()
        {
            Name = name,
            Command = command,
            Ecosystem = ecosystem,
            AppliesWhen = when,
            Scope = AdditionScope.FolderBackground,
            RunMode = RunMode.VisibleTerminal,
        };
}
