# rcmm-convert.ps1 — RCMM "Convert / Change format" smart action.
#
# Launched by the right-click verb:
#   powershell -NoProfile -ExecutionPolicy Bypass -File rcmm-convert.ps1 "<file>"
#
# Detects the file type, checks for the converter tool (offers a winget install
# if it's missing), shows a boxed arrow-key format menu, and runs the conversion.
# Needs a VT-capable terminal for the lime highlight (Windows Terminal / modern
# conhost on Win10 1809+ / Win11).
param([Parameter(Mandatory = $true)][string]$Path)

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

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host "File not found: $Path"
    PauseExit
}

$ext = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()

$imageExts = @('.png', '.jpg', '.jpeg', '.bmp', '.gif', '.webp', '.tif', '.tiff')
$videoExts = @('.mp4', '.mkv', '.mov', '.webm', '.avi', '.m4v', '.wmv')

if ($imageExts -contains $ext) {
    $tool = 'magick'
    $wingetId = 'ImageMagick.ImageMagick'
    $targets = @(
        @{ Label = 'PNG';  Ext = '.png' },
        @{ Label = 'JPG';  Ext = '.jpg' },
        @{ Label = 'WebP'; Ext = '.webp' },
        @{ Label = 'ICO';  Ext = '.ico' }
    )
}
elseif ($videoExts -contains $ext) {
    $tool = 'ffmpeg'
    $wingetId = 'Gyan.FFmpeg'
    $targets = @(
        @{ Label = 'MP4';  Ext = '.mp4' },
        @{ Label = 'MKV';  Ext = '.mkv' },
        @{ Label = 'MOV';  Ext = '.mov' },
        @{ Label = 'WebM'; Ext = '.webm' },
        @{ Label = 'GIF';  Ext = '.gif' },
        @{ Label = 'Audio (MP3)'; Ext = '.mp3'; Extra = @('-vn', '-c:a', 'libmp3lame') }
    )
}
else {
    Write-Host "RCMM can't convert '$ext' files yet."
    PauseExit
}

# Don't offer to convert a file into its own format.
$targets = @($targets | Where-Object { $_.Ext -ne $ext })

# 1. Dependency check (+ optional winget install).
Write-Host "Checking for $tool... " -NoNewline
$found = Get-Command $tool -ErrorAction SilentlyContinue
if (-not $found) {
    Write-Host 'not installed.'
    $ans = Read-Host "Install $tool with winget? [Y/N]"
    if ($ans -eq '' -or $ans -match '^[Yy]') {
        if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
            Write-Host "winget isn't available here. Install $tool manually, then re-run."
            PauseExit
        }
        winget install --id $wingetId -e --accept-source-agreements --accept-package-agreements
        # winget updates the registry PATH, but not this session — refresh it.
        $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
        $found = Get-Command $tool -ErrorAction SilentlyContinue
        if (-not $found) {
            Write-Host "$tool still isn't on PATH. Close this window and try again once the install finishes."
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
                    -Status (([char]0x2713) + " $tool ready") -Items $labels
if ($sel -lt 0) {
    Write-Host ''
    Write-Host 'Cancelled.'
    PauseExit
}
$target = $targets[$sel]

# Output path (don't clobber the source).
$dir = [System.IO.Path]::GetDirectoryName($Path)
$name = [System.IO.Path]::GetFileNameWithoutExtension($Path)
$out = Join-Path $dir ($name + $target.Ext)
if ($out -ieq $Path) {
    $out = Join-Path $dir ($name + ' (converted)' + $target.Ext)
}

Write-Host ''
Write-Host ("Converting -> " + [System.IO.Path]::GetFileName($out) + ' ...')
if ($tool -eq 'ffmpeg') {
    $extra = @()
    if ($target.Extra) { $extra = $target.Extra }
    & $tool -y -i $Path @extra $out
}
else {
    & $tool $Path $out
}

if ($LASTEXITCODE -eq 0) {
    Write-Host ''
    if (Test-Path -LiteralPath $out) {
        Write-Host "Done -> $out"
        Start-Process explorer.exe -ArgumentList ("/select,`"" + $out + "`"")
    }
    else {
        # Some conversions (e.g. an animated GIF -> PNG) emit indexed files like
        # name-0.png; the tool reported success, so don't cry false failure.
        Write-Host 'Done. Output written next to the source.'
    }
}
else {
    Write-Host ''
    Write-Host "Conversion failed (exit $LASTEXITCODE)."
}
PauseExit
