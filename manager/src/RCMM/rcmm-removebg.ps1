# rcmm-removebg.ps1 — RCMM "Remove background" smart action (AI image cutout).
#
# Launched by the right-click verb:
#   powershell -NoProfile -ExecutionPolicy Bypass -File rcmm-removebg.ps1 "<file|folder>"
#
# Uses rembg (https://github.com/danielgatis/rembg). rembg is a Python tool, so
# we run it through uv ("uv tool run") — uv (winget: astral-sh.uv) fetches a
# Python + rembg + the model on first use, with no manual Python/pip setup.
#
# Pickers: model (General / People / High quality), edge refinement (alpha
# matting), and background (transparent, or composited onto a solid colour with
# ImageMagick since rembg's CLI has no background-colour flag). Works on a single
# image or a whole folder. Output is a PNG beside the source.
#
# -DryRun reports tool state + a sample command without installing or running.
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Path,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch {}

function PauseExit([string]$openPath = $null) {
    Write-Host ''
    $canOpen = $openPath -and (Test-Path -LiteralPath $openPath)
    $prompt  = if ($canOpen) { 'Press Enter to open the result and close' } else { 'Press Enter to close' }
    Read-Host $prompt | Out-Null
    # On success, open the output (file -> default app, folder -> Explorer) and exit.
    if ($canOpen) { try { Start-Process $openPath } catch {} }
    exit
}

# Boxed, arrow-key navigable single-label menu. Returns the chosen index or -1.
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

# winget install + PATH refresh, then re-resolve.
function Install-Winget([string]$id, [string]$name, [string[]]$fallbacks) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Write-Host "winget isn't available here. Install $name manually, then re-run."
        return $null
    }
    winget install --id $id -e --accept-source-agreements --accept-package-agreements
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
    return (Resolve-Tool ($name) $fallbacks)
}

function Get-OutPath([string]$in, [string]$suffix, [string]$ext) {
    $dir  = [System.IO.Path]::GetDirectoryName($in)
    $name = [System.IO.Path]::GetFileNameWithoutExtension($in)
    $cand = Join-Path $dir ("{0} ({1}){2}" -f $name, $suffix, $ext)
    $n = 2
    while (Test-Path -LiteralPath $cand) { $cand = Join-Path $dir ("{0} ({1}) ({2}){3}" -f $name, $suffix, $n, $ext); $n++ }
    return $cand
}

function Get-OutDir([string]$in, [string]$suffix) {
    $trimmed = $in.TrimEnd('\')
    $parent  = [System.IO.Path]::GetDirectoryName($trimmed)
    $name    = [System.IO.Path]::GetFileName($trimmed)
    $cand = Join-Path $parent ("{0} ({1})" -f $name, $suffix)
    $n = 2
    while (Test-Path -LiteralPath $cand) { $cand = Join-Path $parent ("{0} ({1}) ({2})" -f $name, $suffix, $n); $n++ }
    return $cand
}

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host "Not found: $Path"
    PauseExit
}

$imageExt = @('.png', '.jpg', '.jpeg', '.webp', '.bmp')
$isFolder = Test-Path -LiteralPath $Path -PathType Container
if ($isFolder) {
    $imgs = @(Get-ChildItem -LiteralPath $Path -File -ErrorAction SilentlyContinue |
              Where-Object { $imageExt -contains $_.Extension.ToLowerInvariant() })
    if ($imgs.Count -eq 0) { Write-Host "No images in this folder (png / jpg / jpeg / webp / bmp)."; PauseExit }
}
else {
    $ext = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()
    if ($imageExt -notcontains $ext) { Write-Host "Remove background works on images, or a folder of images (got '$ext')."; PauseExit }
}

# winget's astral-sh.uv drops uv.exe in the Packages dir without a PATH shim,
# so resolution must look there too.
$uvFallbacks = @(
    '%USERPROFILE%\.local\bin\uv.exe',
    '%LOCALAPPDATA%\Microsoft\WinGet\Links\uv.exe',
    '%LOCALAPPDATA%\Microsoft\WinGet\Packages\astral-sh.uv*\uv.exe')

if ($DryRun) {
    $uv = Resolve-Tool 'uv' $uvFallbacks
    Write-Host ("uv: {0}" -f $(if ($uv) { $uv } else { 'not installed (would winget-install astral-sh.uv on first run)' }))
    $sampleOut = if ($isFolder) { Get-OutDir $Path 'no bg' } else { Get-OutPath $Path 'no bg' '.png' }
    if ($isFolder) { Write-Host ("Folder batch: {0} image(s)" -f $imgs.Count) }
    Write-Host ('Sample (General / transparent): uv tool run rembg i -m u2net "{0}" "{1}"' -f $Path, $sampleOut)
    exit
}

# 1. Ensure uv (runs rembg via "uv tool run", fetching Python + rembg itself).
Write-Host 'Checking for uv... ' -NoNewline
$uv = Resolve-Tool 'uv' $uvFallbacks
if (-not $uv) {
    Write-Host 'not installed.'
    $ans = Read-Host 'Install uv with winget (it runs the background remover)? [Y/N]'
    if ($ans -eq '' -or $ans -match '^[Yy]') {
        $uv = Install-Winget 'astral-sh.uv' 'uv' $uvFallbacks
        if (-not $uv) { Write-Host "uv still isn't available. Try again once the install finishes."; PauseExit }
    }
    else { Write-Host 'Cancelled.'; PauseExit }
}
else { Write-Host 'ok.' }

# 1b. Ensure rembg is installed as a uv tool. (rembg has no inference backend
# without the cpu/gpu extra, so install "rembg[cli,cpu]" — cli + CPU onnxruntime.)
Write-Host 'Checking for rembg... ' -NoNewline
$rembgList = ''
try { $rembgList = (& $uv tool list 2>$null | Out-String) } catch {}
if ($rembgList -notmatch 'rembg') {
    Write-Host 'not installed.'
    $ans = Read-Host 'Install the background remover (rembg) via uv? (downloads rembg + a Python) [Y/N]'
    if ($ans -eq '' -or $ans -match '^[Yy]') {
        & $uv tool install "rembg[cli,cpu]"
        if ($LASTEXITCODE -ne 0) { Write-Host 'rembg install failed.'; PauseExit }
    }
    else { Write-Host 'Cancelled.'; PauseExit }
}
else { Write-Host 'ok.' }

# 2. Model picker.
Clear-Host
$mSel = Show-BoxMenu -Title ("Remove background  " + [System.IO.Path]::GetFileName($Path.TrimEnd('\'))) `
                     -Status (([char]0x2713) + ' uv ready') `
                     -Items @('General', 'People (portrait)', 'High quality (BiRefNet)')
if ($mSel -lt 0) { Write-Host ''; Write-Host 'Cancelled.'; PauseExit }
$model = @('u2net', 'u2net_human_seg', 'birefnet-general')[$mSel]

# 3. Edge refinement.
Clear-Host
$eSel = Show-BoxMenu -Title 'Remove background:  edges' -Status 'refine the cutout edges' `
                     -Items @('Off  (faster)', 'On  (alpha matting: cleaner hair/edges, slower)')
if ($eSel -lt 0) { Write-Host ''; Write-Host 'Cancelled.'; PauseExit }
$alpha = ($eSel -eq 1)

# 4. Background.
Clear-Host
$bSel = Show-BoxMenu -Title 'Remove background:  output' -Status 'what fills the removed area' `
                     -Items @('Transparent (PNG)', 'White', 'Black', 'Green (chroma)')
if ($bSel -lt 0) { Write-Host ''; Write-Host 'Cancelled.'; PauseExit }
$bgColor = @($null, 'white', 'black', '#00FF00')[$bSel]

# Solid backgrounds are composited with ImageMagick (rembg's CLI has no bg flag).
$magick = $null
if ($bgColor) {
    Write-Host 'Checking for ImageMagick... ' -NoNewline
    $magick = Resolve-Tool 'magick' @()
    if (-not $magick) {
        Write-Host 'not installed.'
        $ans = Read-Host 'Install ImageMagick with winget (needed for a solid background)? [Y/N]'
        if ($ans -eq '' -or $ans -match '^[Yy]') {
            $magick = Install-Winget 'ImageMagick.ImageMagick' 'magick' @()
            if (-not $magick) { Write-Host "ImageMagick still isn't available. Try again once the install finishes."; PauseExit }
        }
        else { Write-Host 'Cancelled.'; PauseExit }
    }
    else { Write-Host 'ok.' }
}

# Common rembg args (model + optional alpha matting).
$rembgOpts = @('-m', $model); if ($alpha) { $rembgOpts += '-a' }

Write-Host ''
Write-Host '(the chosen model downloads on first use - BiRefNet is large, can take a minute)'
Write-Host ''

if ($isFolder) {
    $out = Get-OutDir $Path 'no bg'
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    Write-Host ("Removing background from {0} image(s) -> {1} ..." -f $imgs.Count, [System.IO.Path]::GetFileName($out))
    & $uv tool run rembg p @rembgOpts "$Path" "$out"
    $made = @(Get-ChildItem -LiteralPath $out -File -ErrorAction SilentlyContinue)
    if ($made.Count -eq 0) { Write-Host ''; Write-Host "Failed - no output produced."; PauseExit }
    if ($bgColor) {
        Write-Host ("Compositing onto {0} ..." -f $bgColor)
        foreach ($f in $made) { & $magick $f.FullName -background $bgColor -flatten $f.FullName }
    }
    Write-Host ''
    Write-Host ("Done -> {0}  ({1} image(s))" -f $out, $made.Count)
}
else {
    $out = Get-OutPath $Path 'no bg' '.png'
    Write-Host ("Removing background -> {0} ..." -f [System.IO.Path]::GetFileName($out))
    & $uv tool run rembg i @rembgOpts "$Path" "$out"
    if (-not (Test-Path -LiteralPath $out)) { Write-Host ''; Write-Host "Failed (rembg produced no output)."; PauseExit }
    if ($bgColor) {
        Write-Host ("Compositing onto {0} ..." -f $bgColor)
        & $magick "$out" -background $bgColor -flatten "$out"
    }
    Write-Host ''
    Write-Host ("Done -> " + $out)
}
PauseExit $out
