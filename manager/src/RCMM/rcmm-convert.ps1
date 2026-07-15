# rcmm-convert.ps1 — RCMM "Change format" smart action.
#
# Launched by the right-click verb:
#   powershell -NoProfile -ExecutionPolicy Bypass -File rcmm-convert.ps1 "<file>"
#
# Detects the file type, checks for the converter tool (offers a winget install
# if it's missing), shows a boxed arrow-key format menu, and runs the conversion.
# Needs a VT-capable terminal for the lime highlight (Windows Terminal / modern
# conhost on Win10 1809+ / Win11).
#
# -DryRun prints the resolved tool + arguments for every target and exits
# (used to verify command-building without the tools installed).
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Path,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# Render Unicode (box-drawing + the marker) as real glyphs instead of "?" /
# best-fit mojibake from a legacy console codepage.
try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch {}

function PauseExit {
    Write-Host ''
    Read-Host 'Press Enter to close' | Out-Null
    exit
}

# Any unhandled terminating error (EAP=Stop) would otherwise kill the verb-spawned
# console before the user can read it. Route every escape through PauseExit.
trap {
    Write-Host ''
    Write-Host ("Something went wrong: " + $_.Exception.Message)
    PauseExit
}

# --- Display-width helpers (shared TUI box drawing) ----------------------------
# East-Asian wide / fullwidth glyphs occupy two terminal cells but count as one
# UTF-16 unit, so String.Length misaligns the box border for such names. Measure
# real cell width, and pad/truncate by it, so the box stays square and (critically)
# never exceeds the console width and wraps.
function Get-DisplayWidth([string]$s) {
    if (-not $s) { return 0 }
    $w = 0
    foreach ($ch in $s.ToCharArray()) {
        $c = [int]$ch
        if (($c -ge 0x1100 -and $c -le 0x115F) -or ($c -ge 0x2E80 -and $c -le 0xA4CF) -or
            ($c -ge 0xAC00 -and $c -le 0xD7A3) -or ($c -ge 0xF900 -and $c -le 0xFAFF) -or
            ($c -ge 0xFE30 -and $c -le 0xFE4F) -or ($c -ge 0xFF00 -and $c -le 0xFF60) -or
            ($c -ge 0xFFE0 -and $c -le 0xFFE6)) { $w += 2 } else { $w += 1 }
    }
    return $w
}

# Pad or ellipsize $s to exactly $width display cells.
function Fit-Display([string]$s, [int]$width) {
    if ($width -lt 1) { return '' }
    if ((Get-DisplayWidth $s) -gt $width) {
        $budget = $width - 1  # room for the ellipsis
        $out = ''; $w = 0
        foreach ($ch in $s.ToCharArray()) {
            $cw = if ((Get-DisplayWidth ([string]$ch)) -eq 2) { 2 } else { 1 }
            if ($w + $cw -gt $budget) { break }
            $out += $ch; $w += $cw
        }
        $s = $out + ([char]0x2026)
    }
    $pad = $width - (Get-DisplayWidth $s)
    if ($pad -gt 0) { return $s + (' ' * $pad) }
    return $s
}

# Widest inner content we can draw without the box wrapping the console.
function Get-MaxBoxWidth {
    $cw = 76
    try { $c = [Console]::WindowWidth; if ($c -gt 10) { $cw = $c - 4 } } catch {}
    return $cw
}

# Output path next to the source with a new extension, guaranteed not to collide
# with the source or any existing file: name.ext -> "name (converted).ext" -> " (2)"...
function Get-UniqueOutPath([string]$dir, [string]$name, [string]$ext) {
    $cand = Join-Path $dir ($name + $ext)
    if (-not (Test-Path -LiteralPath $cand)) { return $cand }
    $cand = Join-Path $dir ($name + ' (converted)' + $ext)
    $n = 2
    while (Test-Path -LiteralPath $cand) {
        $cand = Join-Path $dir ($name + " (converted $n)" + $ext)
        $n++
    }
    return $cand
}

# Collision-free directory path: "base" -> "base (2)" -> "base (3)"...
function Get-UniqueOutDir([string]$dir, [string]$base) {
    $cand = Join-Path $dir $base
    $n = 2
    while (Test-Path -LiteralPath $cand) {
        $cand = Join-Path $dir ("$base ($n)")
        $n++
    }
    return $cand
}

# Boxed, arrow-key navigable menu. Returns the chosen index, or -1 on Esc.
function Show-BoxMenu {
    param([string]$Title, [string]$Status, [string[]]$Items)

    $E     = [char]27
    $lime  = $E + '[38;2;212;255;58m'
    $dim   = $E + '[38;2;138;138;147m'
    $text  = $E + '[38;2;241;241;243m'
    $reset = $E + '[0m'

    $TL = [char]0x250C; $TR = [char]0x2510; $BL = [char]0x2514; $BR = [char]0x2518
    $VL = [char]0x251C; $VR = [char]0x2524; $V = [char]0x2502
    $arrow = [char]0x25B8

    # Width = widest inner line + margin (min 30), but never wider than the
    # console, or long filenames wrap and shatter the box.
    $width = 30
    foreach ($s in (@($Title, $Status) + $Items)) {
        $dw = (Get-DisplayWidth $s) + 5
        if ($dw -gt $width) { $width = $dw }
    }
    $max = Get-MaxBoxWidth
    if ($width -gt $max) { $width = $max }
    $H = ([string][char]0x2500) * $width

    $sel = 0
    $start = [Console]::CursorTop
    try {
        [Console]::CursorVisible = $false
        while ($true) {
            try { [Console]::SetCursorPosition(0, $start) } catch {}
            $lines = New-Object System.Collections.Generic.List[string]
            $lines.Add("  " + $TL + $H + $TR)
            $lines.Add("  " + $V + $text + (Fit-Display (" " + $Title) $width) + $reset + $V)
            if ($Status) {
                $lines.Add("  " + $V + $dim + (Fit-Display (" " + $Status) $width) + $reset + $V)
            }
            $lines.Add("  " + $VL + $H + $VR)
            for ($i = 0; $i -lt $Items.Count; $i++) {
                if ($i -eq $sel) {
                    $inner = Fit-Display (" " + $arrow + " " + $Items[$i]) $width
                    $lines.Add("  " + $V + $lime + $inner + $reset + $V)
                }
                else {
                    $inner = Fit-Display ("   " + $Items[$i]) $width
                    $lines.Add("  " + $V + $dim + $inner + $reset + $V)
                }
            }
            $lines.Add("  " + $BL + $H + $BR)
            [Console]::Out.Write(($lines -join [Environment]::NewLine) + [Environment]::NewLine)

            $k = [Console]::ReadKey($true)
            switch ($k.Key) {
                'UpArrow'   { $sel = ($sel - 1 + $Items.Count) % $Items.Count }
                'DownArrow' { $sel = ($sel + 1) % $Items.Count }
                'Enter'     { return $sel }
                'Escape'    { return -1 }
            }
        }
    }
    finally { try { [Console]::CursorVisible = $true } catch {} }
}

# Resolve a tool by PATH first, then known install locations (Ghostscript and
# LibreOffice don't reliably add themselves to PATH). Fallbacks may contain
# environment variables and wildcards.
function Resolve-Tool([string]$name, [string[]]$fallbacks) {
    $c = Get-Command $name -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    foreach ($f in $fallbacks) {
        $expanded = [Environment]::ExpandEnvironmentVariables($f)
        $hit = Get-Item -Path $expanded -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

# Output path next to the source, new extension; never overwrites the source OR
# any existing unrelated file (e.g. converting clip.mkv when clip.mp4 exists).
function Get-OutPath([string]$in, [string]$ext) {
    $dir = [System.IO.Path]::GetDirectoryName($in)
    $name = [System.IO.Path]::GetFileNameWithoutExtension($in)
    return Get-UniqueOutPath $dir $name $ext
}

# Category descriptor for a given extension, or $null if unsupported.
function Get-Category([string]$ext) {
    # HEIC/HEIF/AVIF read via ImageMagick's libheif delegate; SVG via its built-in
    # renderer (good enough for simple vectors). All ride the same magick path.
    $image = @('.png', '.jpg', '.jpeg', '.bmp', '.gif', '.webp', '.tif', '.tiff', '.heic', '.heif', '.avif', '.jxl', '.svg', '.tga', '.ppm', '.xcf', '.mpo')
    # Includes "weird downloaded video" containers ffmpeg demuxes natively. .ts is
    # deliberately excluded — it collides with TypeScript, and RCMM's users write code.
    $video = @('.mp4', '.mkv', '.mov', '.webm', '.avi', '.m4v', '.wmv', '.flv', '.mpg', '.mpeg', '.3gp', '.3g2', '.vob', '.mxf', '.asf', '.ogv')
    $audio = @('.mp3', '.wav', '.flac', '.m4a', '.ogg', '.aac', '.wma', '.ac3', '.aiff', '.aif', '.amr')
    $doc   = @('.docx', '.doc', '.odt', '.rtf', '.html', '.htm', '.md')

    if ($image -contains $ext) {
        return @{ Name = 'Image'; Tool = 'magick'; Fallbacks = @(); WingetId = 'ImageMagick.ImageMagick'; ExcludeSource = $true; Targets = @(
                @{ Label = 'PNG';  Ext = '.png';  Kind = 'magick' },
                @{ Label = 'JPG';  Ext = '.jpg';  Kind = 'magick' },
                @{ Label = 'WebP'; Ext = '.webp'; Kind = 'magick' },
                @{ Label = 'AVIF'; Ext = '.avif'; Kind = 'magick' },
                @{ Label = 'JXL';  Ext = '.jxl';  Kind = 'magick' },
                @{ Label = 'TIFF'; Ext = '.tiff'; Kind = 'magick' },
                @{ Label = 'BMP';  Ext = '.bmp';  Kind = 'magick' },
                @{ Label = 'GIF';  Ext = '.gif';  Kind = 'magick' },
                @{ Label = 'ICO';  Ext = '.ico';  Kind = 'magick'; Extra = @('-define', 'icon:auto-resize=256,128,64,48,32,16') },
                @{ Label = 'PDF';  Ext = '.pdf';  Kind = 'magick' }) }
    }
    if ($video -contains $ext) {
        return @{ Name = 'Video'; Tool = 'ffmpeg'; Fallbacks = @(); WingetId = 'Gyan.FFmpeg'; ExcludeSource = $true; Targets = @(
                @{ Label = 'MP4';  Ext = '.mp4';  Kind = 'ffmpeg' },
                @{ Label = 'MKV';  Ext = '.mkv';  Kind = 'ffmpeg' },
                @{ Label = 'MOV';  Ext = '.mov';  Kind = 'ffmpeg' },
                @{ Label = 'WebM'; Ext = '.webm'; Kind = 'ffmpeg' },
                @{ Label = 'AVI';  Ext = '.avi';  Kind = 'ffmpeg' },
                @{ Label = 'GIF';  Ext = '.gif';  Kind = 'ffmpeg' },
                @{ Label = 'Audio (MP3)'; Ext = '.mp3'; Kind = 'ffmpeg'; Extra = @('-vn', '-c:a', 'libmp3lame') },
                @{ Label = 'Audio (M4A)'; Ext = '.m4a'; Kind = 'ffmpeg'; Extra = @('-vn', '-c:a', 'aac') }) }
    }
    if ($audio -contains $ext) {
        return @{ Name = 'Audio'; Tool = 'ffmpeg'; Fallbacks = @(); WingetId = 'Gyan.FFmpeg'; ExcludeSource = $true; Targets = @(
                @{ Label = 'MP3';  Ext = '.mp3';  Kind = 'ffmpeg' },
                @{ Label = 'M4A';  Ext = '.m4a';  Kind = 'ffmpeg' },
                @{ Label = 'AAC';  Ext = '.aac';  Kind = 'ffmpeg' },
                @{ Label = 'OGG';  Ext = '.ogg';  Kind = 'ffmpeg' },
                @{ Label = 'OPUS'; Ext = '.opus'; Kind = 'ffmpeg' },
                @{ Label = 'FLAC'; Ext = '.flac'; Kind = 'ffmpeg' },
                @{ Label = 'WAV';  Ext = '.wav';  Kind = 'ffmpeg' }) }
    }
    if ($ext -eq '.pdf') {
        # MuPDF's mutool — winget-installable (Ghostscript is NOT in winget).
        return @{ Name = 'PDF'; Tool = 'mutool'; Fallbacks = @('%LOCALAPPDATA%\Microsoft\WinGet\Packages\ArtifexSoftware.mutool*\*\mutool.exe'); WingetId = 'ArtifexSoftware.mutool'; ExcludeSource = $false; Targets = @(
                @{ Label = 'Text';            Ext = '.txt'; Kind = 'mutool-text' },
                @{ Label = 'Images (PNG/page)'; Ext = '.png'; Kind = 'mutool-images' },
                @{ Label = 'Compress (smaller PDF)'; Ext = '.pdf'; Kind = 'mutool-compress' }) }
    }
    if ($doc -contains $ext) {
        return @{ Name = 'Document'; Tool = 'soffice'; Fallbacks = @('%ProgramFiles%\LibreOffice\program\soffice.com', '%ProgramFiles(x86)%\LibreOffice\program\soffice.com'); WingetId = 'TheDocumentFoundation.LibreOffice'; ExcludeSource = $true; Targets = @(
                @{ Label = 'PDF';  Ext = '.pdf';  Kind = 'soffice'; Fmt = 'pdf' },
                @{ Label = 'DOCX'; Ext = '.docx'; Kind = 'soffice'; Fmt = 'docx' },
                @{ Label = 'ODT';  Ext = '.odt';  Kind = 'soffice'; Fmt = 'odt' },
                @{ Label = 'HTML'; Ext = '.html'; Kind = 'soffice'; Fmt = 'html' },
                @{ Label = 'Text'; Ext = '.txt';  Kind = 'soffice'; Fmt = 'txt' }) }
    }
    return $null
}

# Build the argument array + expected output for a target. Returns @{ Args; Out }.
# Out is $null when the conversion emits multiple/derived files (judge success
# by exit code instead).
function Build-Invocation($target, [string]$in) {
    switch ($target.Kind) {
        'magick' {
            $out = Get-OutPath $in $target.Ext
            $extra = @(); if ($target.Extra) { $extra = $target.Extra }
            return @{ Args = (@($in) + $extra + @($out)); Out = $out }
        }
        'ffmpeg' {
            $out = Get-OutPath $in $target.Ext
            $extra = @(); if ($target.Extra) { $extra = $target.Extra }
            return @{ Args = (@('-y', '-i', $in) + $extra + @($out)); Out = $out }
        }
        'mutool-text' {
            $out = Get-OutPath $in '.txt'
            return @{ Args = @('draw', '-q', '-F', 'txt', '-o', $out, $in); Out = $out }
        }
        'mutool-images' {
            $dir = [System.IO.Path]::GetDirectoryName($in)
            $name = [System.IO.Path]::GetFileNameWithoutExtension($in)
            # Page images land in their own collision-free folder, so they never
            # clobber a pre-existing "name-1.png" sitting next to the PDF.
            $outdir = Get-UniqueOutDir $dir ($name + ' (pages)')
            $pattern = Join-Path $outdir ($name + '-%d.png')
            return @{ Args = @('draw', '-q', '-F', 'png', '-r', '150', '-o', $pattern, $in); Out = $outdir; MakeDir = $outdir }
        }
        'mutool-compress' {
            $out = Get-OutPath $in '.pdf'
            return @{ Args = @('clean', '-gggg', $in, $out); Out = $out }
        }
        'soffice' {
            $dir = [System.IO.Path]::GetDirectoryName($in)
            $name = [System.IO.Path]::GetFileNameWithoutExtension($in)
            # soffice forces "<name>.<fmt>" into --outdir with no rename option, so
            # convert into a private temp subdir and let the caller move the result
            # to a collision-free final name instead of clobbering an existing file.
            $tmp = Join-Path $dir ('.rcmm-convert-' + [System.Guid]::NewGuid().ToString('N'))
            $produced = Join-Path $tmp ($name + '.' + $target.Fmt)
            $final = Get-UniqueOutPath $dir $name ('.' + $target.Fmt)
            return @{ Args = @('--headless', '--convert-to', $target.Fmt, '--outdir', $tmp, $in); Out = $final; TempDir = $tmp; Produced = $produced }
        }
    }
    return @{ Args = @(); Out = $null }
}

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host "File not found: $Path"
    PauseExit
}

$ext = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()
$cat = Get-Category $ext
if (-not $cat) {
    Write-Host "RCMM can't convert '$ext' files yet."
    PauseExit
}

# Don't offer to convert a file into its own format (where that's just a no-op).
# Normalize alias extensions so e.g. a .tif source doesn't get offered "TIFF"
# (.tiff), a .jpeg source "JPG" (.jpg), or a .htm source "HTML" (.html).
$targets = $cat.Targets
if ($cat.ExcludeSource) {
    $norm = switch ($ext) {
        '.tif'  { '.tiff' } '.jpeg' { '.jpg' } '.htm' { '.html' } '.aif' { '.aiff' }
        default { $ext }
    }
    $targets = @($targets | Where-Object { $_.Ext -ne $ext -and $_.Ext -ne $norm })
}

if ($DryRun) {
    Write-Host ("Category: {0}   Tool: {1}   Winget: {2}" -f $cat.Name, $cat.Tool, $cat.WingetId)
    foreach ($t in $targets) {
        $inv = Build-Invocation $t $Path
        Write-Host ("  [{0}]  {1} {2}   (out: {3})" -f $t.Label, $cat.Tool, ($inv.Args -join ' '), $inv.Out)
    }
    exit
}

# 1. Dependency check (+ optional winget install).
Write-Host ("Checking for {0}... " -f $cat.Tool) -NoNewline
$toolPath = Resolve-Tool $cat.Tool $cat.Fallbacks
if (-not $toolPath) {
    Write-Host 'not installed.'
    $ans = Read-Host ("Install {0} with winget? [Y/n]" -f $cat.Tool)
    if ($ans -eq '' -or $ans -match '^[Yy]') {
        if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
            Write-Host "winget isn't available here. Install $($cat.Tool) manually, then re-run."
            PauseExit
        }
        winget install --id $cat.WingetId -e --accept-source-agreements --accept-package-agreements
        $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
        $toolPath = Resolve-Tool $cat.Tool $cat.Fallbacks
        if (-not $toolPath) {
            Write-Host "$($cat.Tool) still isn't available. Close this window and try again once the install finishes."
            PauseExit
        }
    }
    else {
        Write-Host 'Cancelled.'
        PauseExit
    }
}
else {
    Write-Host 'ok.'
}

# 2. Boxed arrow-key format menu.
Clear-Host
$labels = @($targets | ForEach-Object { $_.Label })
$sel = Show-BoxMenu -Title ("Convert  " + [System.IO.Path]::GetFileName($Path)) `
                    -Status (([char]0x2713) + " $($cat.Tool) ready") -Items $labels
if ($sel -lt 0) {
    Write-Host ''
    Write-Host 'Cancelled.'
    PauseExit
}
$target = $targets[$sel]
$inv = Build-Invocation $target $Path

Write-Host ''
Write-Host ("Converting -> {0} ..." -f $target.Label)
$toolArgs = $inv.Args
# Multi-file (mutool page images) and document (soffice) outputs need their
# target directory to exist before the tool runs.
if ($inv.MakeDir) { New-Item -ItemType Directory -Force -Path $inv.MakeDir | Out-Null }
if ($inv.TempDir) { New-Item -ItemType Directory -Force -Path $inv.TempDir | Out-Null }
& $toolPath @toolArgs
$convExit = $LASTEXITCODE

# soffice wrote into a temp dir; move its output to the collision-free final path.
if ($inv.TempDir) {
    if ($convExit -eq 0 -and (Test-Path -LiteralPath $inv.Produced)) {
        Move-Item -LiteralPath $inv.Produced -Destination $inv.Out -Force
    }
    Remove-Item -LiteralPath $inv.TempDir -Recurse -Force -ErrorAction SilentlyContinue
}

if ($convExit -eq 0) {
    Write-Host ''
    if ($inv.Out -and (Test-Path -LiteralPath $inv.Out)) {
        Write-Host ("Done -> " + $inv.Out)
        # Only auto-open formats a stock Windows 10 1809+/11 is guaranteed to open
        # with a built-in app (Photos, Media Player, Edge, Notepad) — no Store codec
        # or Office required. Anything else (AVIF/JXL/WebM/OGG/OPUS/DOCX/ODT/ICO,
        # multi-file outputs with Out=$null) just reports its path, so we never
        # pop a "how do you want to open this?" prompt or a player that can't decode.
        $autoOpen = @('.pdf', '.txt', '.html', '.htm',
                      '.png', '.jpg', '.jpeg', '.gif', '.bmp', '.tif', '.tiff', '.webp',
                      '.mp3', '.wav', '.flac', '.m4a', '.aac',
                      '.mp4', '.mov', '.avi', '.mkv')
        if ($autoOpen -contains $target.Ext) {
            try { Start-Process -FilePath $inv.Out } catch {}
        }
    }
    else {
        Write-Host 'Done. Output written next to the source.'
    }
}
else {
    Write-Host ''
    Write-Host "Conversion failed (exit $convExit)."
}
PauseExit
