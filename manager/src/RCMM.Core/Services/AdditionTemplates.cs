using System.Collections.Generic;
using System.Linq;
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

        /// <summary>When set on a File-scope template, the added entry registers
        /// only under these extensions (e.g. "png", "mp4") instead of the
        /// catch-all "*", so it appears only on those file types.</summary>
        public IReadOnlyList<string>? FileTypes { get; init; }
    }

    // ---- File-type sets for the media smart actions (so they only appear on
    //      relevant files, not every file). Match what each script accepts. ----
    private static readonly string[] _imageExts = { "png", "jpg", "jpeg", "webp", "bmp" };          // Upscale / Remove background
    private static readonly string[] _videoExts = { "mp4", "mkv", "mov", "webm", "avi", "m4v", "wmv", "flv", "ts", "m2ts", "mpg", "mpeg" }; // Compress
    private static readonly string[] _changeFormatExts =                                            // everything Change format detects
    {
        "png", "jpg", "jpeg", "bmp", "gif", "webp", "tif", "tiff", "heic", "heif", "avif", "jxl", "svg", "tga", "ppm", "xcf", "mpo",
        "mp4", "mkv", "mov", "webm", "avi", "m4v", "wmv", "flv", "mpg", "mpeg", "3gp", "3g2", "vob", "mxf", "asf", "ogv",
        "mp3", "wav", "flac", "m4a", "ogg", "aac", "wma", "ac3", "aiff", "aif", "amr",
        "pdf", "docx", "doc", "odt", "rtf", "html", "htm", "md",
    };
    // Compress handles both: videos (ffmpeg) + images (CaesiumCLT). The script
    // branches on the clicked file's extension.
    private static readonly string[] _compressExts =
        _videoExts.Concat(new[] { "png", "jpg", "jpeg", "webp", "bmp", "gif", "tif", "tiff" }).ToArray();

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
        // Host PowerShell inside Windows Terminal (wt -d "<dir>" powershell) rather
        // than launching powershell.exe with -Command "Set-Location '%V'". Explorer
        // substitutes %V after RCMM is out of the loop, so RCMM cannot escape it; a
        // folder named  '; calc; '  or  $(calc)  injected into that -Command string
        // and ran. wt takes the directory as a plain -d argument it never re-parses
        // as code, closing the injection. Same safe pattern as the AI-CLI launchers.
        Shell("PowerShell here",       "\"%bin%\" -d \"%V\" powershell", "wt.exe", null),
        Shell("Command Prompt here",   "\"%bin%\" /K cd /d \"%V\"", "cmd.exe", null),
        Shell("Git Bash here",         "\"%bin%\" \"--cd=%V\"",     "git-bash.exe", _gitBashPaths),
        Shell("WSL here",              "\"%bin%\" --cd \"%V\"",     "wsl.exe", null),
        Shell("Windows Terminal here", "\"%bin%\" -d \"%V\"",       "wt.exe", null),
        // Tabby (tabby.sh) isn't on PATH, so %bin% resolves via the fallbacks.
        // Its CLI verb `open [directory]` opens a shell in that folder.
        Shell("Open Tabby here",       "\"%bin%\" open \"%V\"",     "Tabby.exe",
              new[] { @"%ProgramFiles%\Tabby\Tabby.exe", @"%LOCALAPPDATA%\Programs\tabby\Tabby.exe" }),

        // AI CLI launchers — open Windows Terminal in the folder and
        // immediately drop into a tool's REPL/session. The trailing token
        // (`claude`, `codex`) is the command wt runs in the default profile.
        // Requires the CLI to be on PATH (`npm install -g @anthropic-ai/claude-code`
        // for Claude; OpenAI Codex CLI for codex).
        Shell("Open Claude here", "\"%bin%\" -d \"%V\" claude", "wt.exe", null, icon: "lib:claude"),
        Shell("Open Codex here",  "\"%bin%\" -d \"%V\" codex",  "wt.exe", null, icon: "lib:openai"),
        Shell("Open Gemini here", "\"%bin%\" -d \"%V\" gemini", "wt.exe", null),
        Shell("Open Aider here",  "\"%bin%\" -d \"%V\" aider",  "wt.exe", null),
        Shell("Open Claude (resume)", "\"%bin%\" -d \"%V\" claude --resume", "wt.exe", null, icon: "lib:claude"),

        // Open an ELEVATED terminal in the folder (Windows' "Open in Terminal"
        // is non-admin only). Routed through rcmm-action.ps1, which self-elevates
        // via UAC. IconBinary=wt.exe supplies the icon only; the command is the
        // hidden launcher, not a direct wt call.
        new Template
        {
            Name = "Open admin Terminal here",
            Command = "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"%selfdir%\\rcmm-action.ps1\" -Action adminterm -Path \"%V\"",
            Ecosystem = "Shell",
            Scope = AdditionScope.FolderBackground,
            RunMode = RunMode.Background,
            IconBinary = "wt.exe",
        },

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

        // Bun
        Cmd("bun install", "bun install", "Bun", "lib:package"),
        Cmd("bun run dev", "bun run dev", "Bun", "lib:package"),

        // pnpm
        Cmd("pnpm install", "pnpm install", "pnpm", "lib:package"),
        Cmd("pnpm dev",     "pnpm dev",     "pnpm", "lib:package"),

        // uv (Python)
        Cmd("uv sync", "uv sync", "uv", "lib:code-square"),
        Cmd("uv venv", "uv venv", "uv", "lib:code-square"),

        // GitHub CLI
        Cmd("gh pr create --web", "gh pr create --web", "GitHub CLI", "lib:git-branch"),
        Cmd("gh repo view --web", "gh repo view --web", "GitHub CLI", "lib:git-branch"),
        Cmd("gh pr list",         "gh pr list",         "GitHub CLI", "lib:git-branch"),

        // Files — smart actions. "Change format" opens a terminal
        // running the shipped rcmm-convert.ps1 on the right-clicked file: it
        // checks for the converter tool (offers a winget install if missing),
        // shows a numbered format menu, and converts. %selfdir% is replaced
        // with RCMM.exe's directory (where the script ships) at +Add; %1 is the
        // clicked file. Scope=File (HKCU\…\*\shell, appears on any file); the
        // script itself rejects types it can't handle. RunMode.Background =
        // launch powershell directly, no extra cmd wrapper.
        new Template
        {
            Name = "Change format",
            Command = "powershell -NoProfile -ExecutionPolicy Bypass -File \"%selfdir%\\rcmm-convert.ps1\" \"%1\"",
            Ecosystem = "Files",
            Scope = AdditionScope.File,
            RunMode = RunMode.Background,
            Icon = "lib:redo-2",
            FileTypes = _changeFormatExts,
        },

        // "Compress" opens a terminal running rcmm-compress.ps1 on the clicked
        // video: it probes the source with ffprobe, then offers boxed pickers
        // for codec / quality (CRF) / resolution (% of source) / audio and
        // re-encodes smaller with ffmpeg. Video-only for now; same %selfdir%/%1
        // wiring as Convert. (A size-target sibling is planned — see ROADMAP.md.)
        new Template
        {
            Name = "Compress",
            Command = "powershell -NoProfile -ExecutionPolicy Bypass -File \"%selfdir%\\rcmm-compress.ps1\" \"%1\"",
            Ecosystem = "Files",
            Scope = AdditionScope.File,
            RunMode = RunMode.Background,
            Icon = "lib:shrink",
            FileTypes = _compressExts,
        },

        // "Upscale" runs rcmm-upscale.ps1 on the clicked image: AI super-
        // resolution via Real-ESRGAN (ncnn/Vulkan), picking a model (photo /
        // anime) and a 2x/3x/4x scale. Unlike Convert/Compress the tool isn't
        // on winget, so the script fetches it from GitHub on first use (needs a
        // Vulkan GPU). Image files only.
        new Template
        {
            Name = "Upscale",
            Command = "powershell -NoProfile -ExecutionPolicy Bypass -File \"%selfdir%\\rcmm-upscale.ps1\" \"%1\"",
            Ecosystem = "Files",
            Scope = AdditionScope.File,
            RunMode = RunMode.Background,
            Icon = "lib:arrow-big-up-dash",
            FileTypes = _imageExts,
        },

        // Same script on a folder right-click (%1 = the clicked folder): it
        // detects the folder and batch-upscales every image inside via
        // Real-ESRGAN's native directory mode, into "<folder> (upscaled Nx)".
        new Template
        {
            Name = "Upscale images",
            Command = "powershell -NoProfile -ExecutionPolicy Bypass -File \"%selfdir%\\rcmm-upscale.ps1\" \"%1\"",
            Ecosystem = "Files",
            Scope = AdditionScope.Folder,
            RunMode = RunMode.Background,
            Icon = "lib:arrow-big-up-dash",
        },

        // "Remove background" — AI image cutout via rembg (run through uv).
        // File scope + image FileTypes so it only appears on image files (the
        // folder-batch option was dropped in favour of this filtering). Pickers:
        // model / edge refinement / background (transparent or composited via
        // ImageMagick).
        new Template
        {
            Name = "Remove background",
            Command = "powershell -NoProfile -ExecutionPolicy Bypass -File \"%selfdir%\\rcmm-removebg.ps1\" \"%1\"",
            Ecosystem = "Files",
            Scope = AdditionScope.File,
            RunMode = RunMode.Background,
            Icon = "lib:eraser",
            FileTypes = _imageExts,
        },

        // File power actions, routed through rcmm-action.ps1 (silent / self-
        // elevating). Scope=File so they sit on file right-clicks.
        new Template
        {
            Name = "Copy SHA-256",
            Icon = "lib:hash",
            Command = "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"%selfdir%\\rcmm-action.ps1\" -Action sha256 -Path \"%1\"",
            Ecosystem = "Files",
            Scope = AdditionScope.File,
            RunMode = RunMode.Background,
        },
        new Template
        {
            Name = "Unblock file",
            Icon = "lib:shield-check",
            Command = "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"%selfdir%\\rcmm-action.ps1\" -Action unblock -Path \"%1\"",
            Ecosystem = "Files",
            Scope = AdditionScope.File,
            RunMode = RunMode.Background,
        },
        new Template
        {
            Name = "Take ownership",
            Icon = "lib:key",
            Command = "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"%selfdir%\\rcmm-action.ps1\" -Action takeown -Path \"%1\"",
            Ecosystem = "Files",
            Scope = AdditionScope.File,
            RunMode = RunMode.Background,
        },
    };

    /// <summary>Owner-curated "proudest work" lineup for the ★ Featured chip on
    /// the Browse-templates page, in display order. Each name must match an
    /// entry in <see cref="All"/> — a test guards this so a rename can't silently
    /// drop one. Featured entries still also appear under their normal chip.</summary>
    public static readonly IReadOnlyList<string> Featured = new[]
    {
        "Upscale", "Compress", "Change format", "Open Claude here", "Open Codex here",
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
