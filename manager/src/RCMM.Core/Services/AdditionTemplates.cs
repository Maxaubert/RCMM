using System.Collections.Generic;
using RCMM.Core.Models;

namespace RCMM.Core.Services;

/// <summary>
/// Static catalogue of predefined entry templates shipped with RCMM. Not persisted —
/// when the user "Adds" a template, a fresh AdditionEntry is created with the
/// template's defaults; the link is severed and the new entry is fully editable.
///
/// All templates target FolderBackground (right-click empty space in a folder)
/// since the catalogue is currently "do something in this directory" commands.
///
/// Section order is the order of the entries in <see cref="All"/> — the
/// Templates browser uses <c>GroupBy(Ecosystem)</c> which preserves
/// first-appearance order. Tweak by reordering rows here.
/// </summary>
public static class AdditionTemplates
{
    public sealed record Template
    {
        public required string Name { get; init; }
        public required string Command { get; init; }
        public required string Ecosystem { get; init; }
        public required AdditionScope Scope { get; init; }
        public required RunMode RunMode { get; init; }
        public string WorkingDir { get; init; } = "%V";

        /// <summary>Library icon to use if no binary-icon lookup applies
        /// (or if the binary lookup fails). Pass a <c>lib:name</c> string.</summary>
        public string? Icon { get; init; }

        /// <summary>If set, the Templates page resolves this binary on +Add via
        /// <see cref="BinaryResolver"/> and uses its absolute path as the
        /// entry's Icon (Windows extracts the icon from the .exe). The binary's
        /// resolved path also substitutes for <c>%bin%</c> in the Command.</summary>
        public string? IconBinary { get; init; }

        /// <summary>Fallback install paths for <see cref="IconBinary"/> when
        /// PATH lookup fails. Environment variables are expanded.</summary>
        public IReadOnlyList<string>? IconBinaryFallbacks { get; init; }
    }

    // ---- Well-known install paths (declared before All so the catalogue's
    //      static initializer can read them in order). -----------------------

    private static readonly IReadOnlyList<string> _vscodePaths = new[]
    {
        @"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe",
        @"%ProgramFiles%\Microsoft VS Code\Code.exe",
        @"%ProgramFiles(x86)%\Microsoft VS Code\Code.exe",
    };

    private static readonly IReadOnlyList<string> _cursorPaths = new[]
    {
        @"%LOCALAPPDATA%\Programs\cursor\Cursor.exe",
        @"%LOCALAPPDATA%\Programs\Cursor\Cursor.exe",
        @"%ProgramFiles%\Cursor\Cursor.exe",
    };

    private static readonly IReadOnlyList<string> _windsurfPaths = new[]
    {
        @"%LOCALAPPDATA%\Programs\Windsurf\Windsurf.exe",
        @"%ProgramFiles%\Windsurf\Windsurf.exe",
    };

    private static readonly IReadOnlyList<string> _gitBashPaths = new[]
    {
        @"%ProgramFiles%\Git\git-bash.exe",
        @"%ProgramFiles(x86)%\Git\git-bash.exe",
        @"%LOCALAPPDATA%\Programs\Git\git-bash.exe",
    };

    // ---- Section-by-section catalogue (display order matters) ---------------

    public static IReadOnlyList<Template> All { get; } = new List<Template>
    {
        // Git
        Cmd("git pull",                "git pull",                "Git", "lib:git-branch"),
        Cmd("git push",                "git push",                "Git", "lib:git-branch"),
        Cmd("git status",              "git status",              "Git", "lib:git-branch"),
        Cmd("git fetch --all",         "git fetch --all",         "Git", "lib:git-branch"),
        Cmd("git log --oneline -20",   "git log --oneline -20",   "Git", "lib:git-branch"),
        Cmd("git diff",                "git diff",                "Git", "lib:git-branch"),
        Cmd("git stash",               "git stash",               "Git", "lib:git-branch"),
        Cmd("git stash pop",           "git stash pop",           "Git", "lib:git-branch"),
        Cmd("git branch -a",           "git branch -a",           "Git", "lib:git-branch"),

        // Node
        Cmd("npm run dev",             "npm run dev",             "Node", "lib:package"),
        Cmd("npm install",             "npm install",             "Node", "lib:package"),
        Cmd("npm run build",           "npm run build",           "Node", "lib:package"),
        Cmd("npm test",                "npm test",                "Node", "lib:package"),

        // "Open project" — GUI editors that open the folder. RunMode is
        // Background because each launches its own window. All commands use
        // the resolved absolute %bin% path: the editors ship a .cmd wrapper
        // on PATH (code.cmd, cursor.cmd, windsurf.cmd) which Windows' shell-
        // verb runner can't invoke without a cmd.exe wrapper, but the real
        // .exe runs directly.
        Editor("Open project in VS Code",  "\"%bin%\" \"%V\"", "Code.exe", _vscodePaths),
        Editor("Open project in Cursor",   "\"%bin%\" \"%V\"", "Cursor.exe", _cursorPaths),
        Editor("Open project in Windsurf", "\"%bin%\" \"%V\"", "Windsurf.exe", _windsurfPaths),

        // "Shell" — terminal/shell launchers. Absolute paths via %bin% for
        // the same robustness reason (no PATH dependency, no app-execution-
        // alias quirks for wt.exe).
        Shell("PowerShell here",       "\"%bin%\" -NoExit -Command \"Set-Location -LiteralPath '%V'\"", "powershell.exe", null),
        Shell("Command Prompt here",   "\"%bin%\" /K cd /d \"%V\"", "cmd.exe", null),
        Shell("Git Bash here",         "\"%bin%\" \"--cd=%V\"",     "git-bash.exe", _gitBashPaths),
        Shell("WSL here",              "\"%bin%\" --cd \"%V\"",     "wsl.exe", null),
        Shell("Windows Terminal here", "\"%bin%\" -d \"%V\"",       "wt.exe", null),

        // AI CLI launchers — open Windows Terminal in the folder and
        // immediately drop into a tool's REPL/session. The trailing token
        // (`claude`, `codex`) is the command wt runs in the default profile.
        // Requires the CLI to be on PATH (`npm install -g @anthropic-ai/claude-code`
        // for Claude; OpenAI Codex CLI for codex).
        Shell("Open Claude here", "\"%bin%\" -d \"%V\" claude", "wt.exe", null, icon: "lib:claude"),
        Shell("Open Codex here",  "\"%bin%\" -d \"%V\" codex",  "wt.exe", null, icon: "lib:openai"),

        // Python
        Cmd("python -m venv .venv",            "python -m venv .venv",            "Python", "lib:code-square"),
        Cmd("pip install -r requirements",     "pip install -r requirements.txt", "Python", "lib:code-square"),
        Cmd("pytest",                          "pytest",                          "Python", "lib:code-square"),

        // .NET
        Cmd("dotnet build", "dotnet build", ".NET", "lib:code-square"),
        Cmd("dotnet run",   "dotnet run",   ".NET", "lib:code-square"),
        Cmd("dotnet test",  "dotnet test",  ".NET", "lib:code-square"),

        // Rust
        Cmd("cargo run",   "cargo run",   "Rust", "lib:settings"),
        Cmd("cargo build", "cargo build", "Rust", "lib:settings"),
        Cmd("cargo test",  "cargo test",  "Rust", "lib:settings"),

        // Go
        Cmd("go run",   "go run .", "Go", "lib:zap"),
        Cmd("go build", "go build", "Go", "lib:zap"),
    };

    // ---- Section / kind helpers ---------------------------------------------

    private static Template Cmd(string name, string command, string ecosystem, string icon)
        => new()
        {
            Name = name,
            Command = command,
            Ecosystem = ecosystem,
            Scope = AdditionScope.FolderBackground,
            RunMode = RunMode.VisibleTerminal,
            Icon = icon,
        };

    /// <summary>GUI editor that opens the folder; no terminal wrapper. Icon
    /// comes from the resolved binary (extracted from .exe resources).</summary>
    private static Template Editor(string name, string command, string binaryName,
                                   IReadOnlyList<string> fallbacks)
        => new()
        {
            Name = name,
            Command = command,
            Ecosystem = "Open project",
            Scope = AdditionScope.FolderBackground,
            RunMode = RunMode.Background,
            IconBinary = binaryName,
            IconBinaryFallbacks = fallbacks,
        };

    /// <summary>Terminal/shell launcher. Same shape as <see cref="Editor"/> but
    /// in its own Ecosystem so the Templates page can filter editors/shells
    /// independently via the chip row. An optional <paramref name="icon"/>
    /// (library ref like "lib:claude") wins over the binary's icon — used by
    /// AI-CLI launchers so the entry shows the tool's brand mark rather than
    /// the host terminal's icon.</summary>
    private static Template Shell(string name, string command, string binaryName,
                                  IReadOnlyList<string>? fallbacks,
                                  string? icon = null)
        => new()
        {
            Name = name,
            Command = command,
            Ecosystem = "Shell",
            Scope = AdditionScope.FolderBackground,
            RunMode = RunMode.Background,
            Icon = icon,
            IconBinary = binaryName,
            IconBinaryFallbacks = fallbacks,
        };

}
