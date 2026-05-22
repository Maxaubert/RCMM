# RCMM Roadmap

A living plan for RCMM — the *what next* and *why*, kept lightweight (not a spec). Scope stays bounded to **right-click-menu management**; see `CLAUDE.md`. When an item is prioritized to build, it graduates to its own brainstorm → `docs/superpowers/specs/` → `docs/superpowers/plans/` cycle.

**Status:** 🔭 idea · 📋 planned · 🚧 in progress · ✅ shipped

---

## 1. Browse templates — new template backlog

The "Browse templates" page ships a static catalogue (`AdditionTemplates.All`) of predefined "Add to RCM" entries. Today it's all developer tooling (Git / Node / Python / .NET / Rust / Go + editors + shells), every entry scoped to `Directory\Background`, under three chips: *Dev tools · Open project · Shell*. The backlog below breaks out of those chips — it needs new categories (update the chip row + ecosystem→chip grouping in `TemplatesPage.xaml.cs`), and a few model additions (§1a).

### 1a. Model additions these need

- 🔭 **File-scope templates (`%1`)** — every current template is `FolderBackground` (`%V`); file-targeted actions need `Scope = File` + the clicked-path placeholder `%1` + per-extension scoping (`FileTypes` exists — verify `AdditionApplier` substitutes `%1`).
- 🔭 **Hidden run mode** — a third `RunMode` that runs without a console window flashing (clipboard / hash / unblock actions need this).
- 🔭 **Elevated flag** — verbs that run as admin (UAC shield + elevating launch): admin terminal, take ownership, run-as-admin.

### 1b. Simple templates (plain registry verbs — no helper)

**Path & clipboard**
- Copy full path (quoted) · Copy file name (with / without extension)
- ⭐ Copy as POSIX/WSL path (`C:\foo\bar` → `/c/foo/bar`)
- ⭐ Copy as Markdown link `[name](path)`
- Copy as `file://` URL · Copy SHA-256 to clipboard (MD5 / SHA-1 variants)

**File actions** (`Scope = File`, `%1`)
- Open with VS Code / Cursor / Notepad
- ⭐ Unblock file (strip Mark-of-the-Web — `Unblock-File`)
- Run as administrator (.exe/.bat/.ps1, Elevated) · Run this script (.py/.js/.ps1) in a terminal
- Make a dated backup copy (`file.ext.YYYY-MM-DD.bak`) · Toggle read-only

**Folder actions** (`FolderBackground` / `Folder`)
- ⭐ Open elevated terminal here (PowerShell / cmd / wt as admin, Elevated)
- ⭐ Serve this folder (`python -m http.server` / `npx serve`)
- ⭐ Copy folder tree (`tree /F` → clipboard)
- Open in WSL here · Open in new Explorer window · Add folder to PATH · Take ownership (Elevated)

**Archive** (built-in `tar` / `Expand-Archive`; 7-Zip if present)
- Zip this folder · Extract here · Extract to subfolder

**More dev** (`FolderBackground`)
- ⭐ Open repo on GitHub (`git remote` → browser) · Copy repo remote URL
- Docker (`compose up -d` / `down` / `ps`) · bun · pnpm · uv · GitHub CLI (`gh pr create --web`)
- Format with prettier / black / `dotnet format`

**AI CLIs** (extend the Claude/Codex `wt` pattern)
- Open Gemini / Aider here · Open Claude (resume)

### 1c. Smart actions (helper-backed — see §2)

These can't be plain verbs — they invoke the **`rcmm-run` helper (§2)** for type-detection, a picker, and auto-install of any missing tool.

- ⭐ **Convert / Change format** — right-click any media/doc file; the helper detects the type and offers valid targets:
  - **video** → MP4 / MKV / MOV / WebM / GIF / extract-audio (ffmpeg)
  - **image** → PNG / WebP / JPG / ICO / resize / strip-EXIF (ImageMagick)
  - **doc** → PDF / merge / split / compress (qpdf / Ghostscript)
- Future type-aware actions reuse the same helper + picker.

### 1d. Out of scope (guard)

Leaning **out** — general Windows tweaking, not menu work: Pin to Start / taskbar, Set as wallpaper, Empty Recycle Bin. Revisit only if we change our mind.

---

## 2. Smart-actions helper (`rcmm-run`) — new initiative

The enabler for §1c. A small helper that ships with RCMM and is invoked by a "smart" template's verb (e.g. `command = "rcmm-run.exe convert "%1""`). Responsibilities:

- **Type detection** — map the clicked file's extension to a category (video / image / doc / archive / …) and its valid target options.
- **Picker UI** — a compact dark dialog (matching RCMM's flat dark look) listing valid targets; user picks → run.
- **Dependency check + auto-install** — resolve the required tool (reuse `BinaryResolver`); if missing, prompt **"Install ffmpeg? [Yes]"** → `winget install <pkg>` (elevated via UAC), then proceed. Mapping e.g. ffmpeg → `Gyan.FFmpeg`, ImageMagick → `ImageMagick.ImageMagick`, 7-Zip → `7zip.7zip`, qpdf → `qpdf.qpdf`.
- **Run + report** — invoke the tool, show progress/result, surface errors instead of a silent fail.

**Why it exists:** a registry verb can only run a fixed command — it can't detect a type, pop a picker, or install a missing dependency. This is a deliberate, owner-approved step beyond "manage the menu" into "perform the action + manage its dependencies."

**Open design questions (resolve when prioritized):**
- Separate `rcmm-run.exe`, or `RCMM.exe` with a CLI subcommand mode? (Separate exe keeps the GUI lean; both ship in one install.)
- Installer backend: **winget** as primary (built into modern Windows); fall back to scoop/choco if present, else a guided manual download. Each install = one UAC prompt.
- Picker UI: native WinUI dialog vs. a lightweight standalone window — must match the dark utility aesthetic.
- Where the **file-type → targets → tool + args** matrix lives (a small data table the helper reads, easy to extend).
- Sizable sub-project — gets its own brainstorm → spec → plan when we pick it up.

---

## 3. Add to RCM — broader feature

The full custom-entry editor (partly built on the Add page). 📋 planned per `CLAUDE.md`. Backlog: input validation for empty/duplicate names (AUDIT.md **H6**), command-quoting safety for `%V`/`%1` (AUDIT.md **H5**), honor-or-remove the unused **Working dir** field (AUDIT.md **M-A4**), submenu / nested-group polish.

## 4. Manage New > submenu

📋 planned per `CLAUDE.md`. Let users manage the "New >" submenu and register new file-type templates (e.g. `.md`). Not yet scoped.

## 5. Hardening (from AUDIT.md)

Full audit in `AUDIT.md`. Roadmap-level follow-ups: ✅ C1/H1 threading (PR #1) · 🔭 H3 cascade-protection sweep correctness · 🔭 H5/H6 Add-to-RCM injection + validation · 🔭 H8 untested icon/path parsers · 🔭 X1 `MainViewModel` decomposition.
