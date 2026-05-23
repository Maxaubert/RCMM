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

    # Width = widest inner line + margin (min 30).
    $width = 30
    foreach ($s in (@($Title, $Status) + $Items)) {
        if ($s -and ($s.Length + 5) -gt $width) { $width = $s.Length + 5 }
    }
    $H = ([string][char]0x2500) * $width

    $sel = 0
    $start = [Console]::CursorTop
    try {
        [Console]::CursorVisible = $false
        while ($true) {
            try { [Console]::SetCursorPosition(0, $start) } catch {}
            $lines = New-Object System.Collections.Generic.List[string]
            $lines.Add("  " + $TL + $H + $TR)
            $lines.Add("  " + $V + $text + (" " + $Title).PadRight($width) + $reset + $V)
            if ($Status) {
                $lines.Add("  " + $V + $dim + (" " + $Status).PadRight($width) + $reset + $V)
            }
            $lines.Add("  " + $VL + $H + $VR)
            for ($i = 0; $i -lt $Items.Count; $i++) {
                if ($i -eq $sel) {
                    $inner = (" " + $arrow + " " + $Items[$i]).PadRight($width)
                    $lines.Add("  " + $V + $lime + $inner + $reset + $V)
                }
                else {
                    $inner = ("   " + $Items[$i]).PadRight($width)
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

# Output path next to the source, new extension; never overwrites the source.
function Get-OutPath([string]$in, [string]$ext) {
    $cand = [System.IO.Path]::ChangeExtension($in, $ext)
    if ($cand -ieq $in) {
        $dir = [System.IO.Path]::GetDirectoryName($in)
        $name = [System.IO.Path]::GetFileNameWithoutExtension($in)
        $cand = Join-Path $dir ($name + ' (converted)' + $ext)
    }
    return $cand
}

# Category descriptor for a given extension, or $null if unsupported.
function Get-Category([string]$ext) {
    $image = @('.png', '.jpg', '.jpeg', '.bmp', '.gif', '.webp', '.tif', '.tiff')
    $video = @('.mp4', '.mkv', '.mov', '.webm', '.avi', '.m4v', '.wmv')
    $audio = @('.mp3', '.wav', '.flac', '.m4a', '.ogg', '.aac', '.wma')
    $doc   = @('.docx', '.doc', '.odt', '.rtf', '.html', '.htm', '.md')

    if ($image -contains $ext) {
        return @{ Name = 'Image'; Tool = 'magick'; Fallbacks = @(); WingetId = 'ImageMagick.ImageMagick'; ExcludeSource = $true; Targets = @(
                @{ Label = 'PNG';  Ext = '.png';  Kind = 'magick' },
                @{ Label = 'JPG';  Ext = '.jpg';  Kind = 'magick' },
                @{ Label = 'WebP'; Ext = '.webp'; Kind = 'magick' },
                @{ Label = 'ICO';  Ext = '.ico';  Kind = 'magick' },
                @{ Label = 'PDF';  Ext = '.pdf';  Kind = 'magick' }) }
    }
    if ($video -contains $ext) {
        return @{ Name = 'Video'; Tool = 'ffmpeg'; Fallbacks = @(); WingetId = 'Gyan.FFmpeg'; ExcludeSource = $true; Targets = @(
                @{ Label = 'MP4';  Ext = '.mp4';  Kind = 'ffmpeg' },
                @{ Label = 'MKV';  Ext = '.mkv';  Kind = 'ffmpeg' },
                @{ Label = 'MOV';  Ext = '.mov';  Kind = 'ffmpeg' },
                @{ Label = 'WebM'; Ext = '.webm'; Kind = 'ffmpeg' },
                @{ Label = 'GIF';  Ext = '.gif';  Kind = 'ffmpeg' },
                @{ Label = 'Audio (MP3)'; Ext = '.mp3'; Kind = 'ffmpeg'; Extra = @('-vn', '-c:a', 'libmp3lame') }) }
    }
    if ($audio -contains $ext) {
        return @{ Name = 'Audio'; Tool = 'ffmpeg'; Fallbacks = @(); WingetId = 'Gyan.FFmpeg'; ExcludeSource = $true; Targets = @(
                @{ Label = 'MP3';  Ext = '.mp3';  Kind = 'ffmpeg' },
                @{ Label = 'WAV';  Ext = '.wav';  Kind = 'ffmpeg' },
                @{ Label = 'FLAC'; Ext = '.flac'; Kind = 'ffmpeg' },
                @{ Label = 'M4A';  Ext = '.m4a';  Kind = 'ffmpeg' },
                @{ Label = 'OGG';  Ext = '.ogg';  Kind = 'ffmpeg' }) }
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
            return @{ Args = @($in, $out); Out = $out }
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
            $pattern = Join-Path $dir ($name + '-%d.png')
            return @{ Args = @('draw', '-q', '-F', 'png', '-r', '150', '-o', $pattern, $in); Out = $null }
        }
        'mutool-compress' {
            $out = Get-OutPath $in '.pdf'
            return @{ Args = @('clean', '-gggg', $in, $out); Out = $out }
        }
        'soffice' {
            $dir = [System.IO.Path]::GetDirectoryName($in)
            $name = [System.IO.Path]::GetFileNameWithoutExtension($in)
            $out = Join-Path $dir ($name + '.' + $target.Fmt)
            return @{ Args = @('--headless', '--convert-to', $target.Fmt, '--outdir', $dir, $in); Out = $out }
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
$targets = $cat.Targets
if ($cat.ExcludeSource) {
    $targets = @($targets | Where-Object { $_.Ext -ne $ext })
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
    $ans = Read-Host ("Install {0} with winget? [Y/N]" -f $cat.Tool)
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
& $toolPath @toolArgs

if ($LASTEXITCODE -eq 0) {
    Write-Host ''
    if ($inv.Out -and (Test-Path -LiteralPath $inv.Out)) {
        Write-Host ("Done -> " + $inv.Out)
        Start-Process explorer.exe -ArgumentList ("/select,`"" + $inv.Out + "`"")
    }
    else {
        Write-Host 'Done. Output written next to the source.'
    }
}
else {
    Write-Host ''
    Write-Host "Conversion failed (exit $LASTEXITCODE)."
}
PauseExit
