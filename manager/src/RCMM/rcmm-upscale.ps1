# rcmm-upscale.ps1 — RCMM "Upscale" smart action (AI image upscaling).
#
# Launched by the right-click verb:
#   powershell -NoProfile -ExecutionPolicy Bypass -File rcmm-upscale.ps1 "<image>"
#
# Uses Real-ESRGAN (ncnn/Vulkan) — a portable, self-contained AI upscaler.
# It isn't on winget, so unlike Convert/Compress this fetches it from the
# project's GitHub release (one self-contained zip with the exe + models +
# Vulkan dlls) into %LOCALAPPDATA%\RCMM\tools\realesrgan on first use.
#
# Requires a Vulkan-capable GPU (Intel/AMD/Nvidia, up-to-date driver). The user
# picks a model (Photo / Anime) and a scale (2x/3x/4x); output is a PNG next to
# the source. Image files only.
#
# -DryRun reports whether the tool is installed and prints a sample command,
# without downloading or upscaling.
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Path,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch {}

# The self-contained Windows zip (exe + bundled models) is a release ASSET on
# the main repo — the dedicated -ncnn-vulkan repo's zip ships code only, no
# models. The asset lives on an older tagged release, so we scan all releases
# for it rather than using /releases/latest.
$Repo    = 'xinntao/Real-ESRGAN'
$ToolDir = Join-Path $env:LOCALAPPDATA 'RCMM\tools\realesrgan'
$ExeName = 'realesrgan-ncnn-vulkan.exe'
$X       = [char]0x00D7   # ×

function PauseExit {
    Write-Host ''
    Read-Host 'Press Enter to close' | Out-Null
    exit
}

# Boxed, arrow-key navigable single-label menu. Returns the chosen index, or -1
# on Esc. (Same look as the Convert/Compress menus.)
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

# Find a *usable* realesrgan-ncnn-vulkan.exe — one with its models/ folder
# alongside (a code-only build without models doesn't count, so a bad/partial
# install triggers a re-download instead of failing at runtime).
function Resolve-Upscaler {
    $hits = Get-ChildItem -Path $ToolDir -Filter $ExeName -Recurse -ErrorAction SilentlyContinue
    foreach ($h in $hits) {
        if (Test-Path (Join-Path $h.DirectoryName 'models')) { return $h.FullName }
    }
    $c = Get-Command 'realesrgan-ncnn-vulkan' -ErrorAction SilentlyContinue
    if ($c -and (Test-Path (Join-Path ([System.IO.Path]::GetDirectoryName($c.Source)) 'models'))) { return $c.Source }
    return $null
}

# Download + extract the latest self-contained Windows build from GitHub.
function Install-Upscaler {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $headers = @{ 'User-Agent' = 'RCMM' }   # GitHub API rejects requests with no UA
    $rels = $null
    try { $rels = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases" -Headers $headers }
    catch { Write-Host "Couldn't reach GitHub: $($_.Exception.Message)"; return $false }
    # Newest release first; take the first ncnn-vulkan Windows zip (it bundles models).
    $asset = $null
    foreach ($r in $rels) {
        $asset = $r.assets | Where-Object {
            $_.name -match 'ncnn-vulkan' -and $_.name -match 'windows' -and $_.name -match '\.zip$'
        } | Select-Object -First 1
        if ($asset) { break }
    }
    if (-not $asset) { Write-Host 'No Windows ncnn-vulkan build found in releases.'; return $false }

    New-Item -ItemType Directory -Force -Path $ToolDir | Out-Null
    # Wipe any stale/partial prior install (e.g. a code-only zip without models).
    Get-ChildItem $ToolDir -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    $zip = Join-Path $env:TEMP $asset.name
    Write-Host ("Downloading {0} ({1:0} MB) ..." -f $asset.name, ($asset.size / 1MB))
    $oldProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'   # IWR's progress bar makes 5.1 downloads crawl
    try { Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -Headers $headers }
    finally { $ProgressPreference = $oldProgress }
    Write-Host 'Extracting ...'
    Expand-Archive -Path $zip -DestinationPath $ToolDir -Force
    Remove-Item $zip -ErrorAction SilentlyContinue
    return $true
}

# Output PNG next to the source; never overwrites.
function Get-OutPath([string]$in, [int]$scale) {
    $dir  = [System.IO.Path]::GetDirectoryName($in)
    $name = [System.IO.Path]::GetFileNameWithoutExtension($in)
    $cand = Join-Path $dir ("{0} (upscaled {1}x).png" -f $name, $scale)
    $n = 2
    while (Test-Path -LiteralPath $cand) {
        $cand = Join-Path $dir ("{0} (upscaled {1}x) ({2}).png" -f $name, $scale, $n)
        $n++
    }
    return $cand
}

# Batch output directory beside the source folder; never overwrites.
function Get-OutDir([string]$in, [int]$scale) {
    $trimmed = $in.TrimEnd('\')
    $parent  = [System.IO.Path]::GetDirectoryName($trimmed)
    $name    = [System.IO.Path]::GetFileName($trimmed)
    $cand = Join-Path $parent ("{0} (upscaled {1}x)" -f $name, $scale)
    $n = 2
    while (Test-Path -LiteralPath $cand) {
        $cand = Join-Path $parent ("{0} (upscaled {1}x) ({2})" -f $name, $scale, $n)
        $n++
    }
    return $cand
}

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host "Not found: $Path"
    PauseExit
}

$imageExt = @('.png', '.jpg', '.jpeg', '.webp', '.bmp')
# A folder right-click batch-upscales every image inside (Real-ESRGAN's native
# directory mode); a file upscales just that image. Same script, both scopes.
$isFolder = Test-Path -LiteralPath $Path -PathType Container

if ($isFolder) {
    $imgs = @(Get-ChildItem -LiteralPath $Path -File -ErrorAction SilentlyContinue |
              Where-Object { $imageExt -contains $_.Extension.ToLowerInvariant() })
    if ($imgs.Count -eq 0) {
        Write-Host "No images to upscale in this folder (png / jpg / jpeg / webp / bmp)."
        PauseExit
    }
}
else {
    $ext = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()
    if ($imageExt -notcontains $ext) {
        Write-Host "RCMM can only upscale images, or a folder of images (got '$ext')."
        PauseExit
    }
}

# DryRun: report state + a sample command, never prompt or download.
if ($DryRun) {
    $exe = Resolve-Upscaler
    Write-Host ("Tool: {0}" -f $(if ($exe) { $exe } else { 'not installed (would download from GitHub on first run)' }))
    $sampleOut = if ($isFolder) { Get-OutDir $Path 4 } else { Get-OutPath $Path 4 }
    if ($isFolder) { Write-Host ("Folder batch: {0} image(s)" -f $imgs.Count) }
    Write-Host 'Sample (Photo / 4x):'
    Write-Host ("  realesrgan-ncnn-vulkan -i `"{0}`" -o `"{1}`" -n realesrgan-x4plus -s 4 -f png" -f $Path, $sampleOut)
    exit
}

# 1. Dependency check (+ optional GitHub-release download).
Write-Host 'Checking for Real-ESRGAN... ' -NoNewline
$exe = Resolve-Upscaler
if (-not $exe) {
    Write-Host 'not installed.'
    Write-Host 'Real-ESRGAN is a ~50 MB download and needs a Vulkan-capable GPU.'
    $ans = Read-Host 'Download it now from GitHub? [Y/N]'
    if ($ans -eq '' -or $ans -match '^[Yy]') {
        if (-not (Install-Upscaler)) { PauseExit }
        $exe = Resolve-Upscaler
        if (-not $exe) { Write-Host "Install finished but the tool wasn't found. Try again."; PauseExit }
    }
    else { Write-Host 'Cancelled.'; PauseExit }
}
else { Write-Host 'ok.' }

# 2. Model picker.
Clear-Host
$targetLabel = if ($isFolder) {
    ("{0}  ({1} images)" -f [System.IO.Path]::GetFileName($Path.TrimEnd('\')), $imgs.Count)
} else {
    [System.IO.Path]::GetFileName($Path)
}
$mSel = Show-BoxMenu -Title ("Upscale  " + $targetLabel) `
                     -Status (([char]0x2713) + ' Real-ESRGAN ready') `
                     -Items @('Photo / realistic', 'Anime, art & line art')
if ($mSel -lt 0) { Write-Host ''; Write-Host 'Cancelled.'; PauseExit }
$model = @('realesrgan-x4plus', 'realesrgan-x4plus-anime')[$mSel]
$modelLabel = @('Photo', 'Anime')[$mSel]

# 3. Scale picker.
Clear-Host
$sSel = Show-BoxMenu -Title 'Upscale  ·  scale' -Status ("Model: " + $modelLabel) `
                     -Items @("2$X  (faster)", "3$X", "4$X  (max detail, slower)")
if ($sSel -lt 0) { Write-Host ''; Write-Host 'Cancelled.'; PauseExit }
$scale = @(2, 3, 4)[$sSel]

# Folder -> a new output directory (Real-ESRGAN needs it to exist); file -> a path.
if ($isFolder) {
    $out = Get-OutDir $Path $scale
    New-Item -ItemType Directory -Force -Path $out | Out-Null
}
else {
    $out = Get-OutPath $Path $scale
}

Write-Host ''
if ($isFolder) {
    Write-Host ("Upscaling {0} image(s) {1}{2} -> {3} ..." -f $imgs.Count, $scale, $X, [System.IO.Path]::GetFileName($out))
}
else {
    Write-Host ("Upscaling {0}{1} -> {2} ..." -f $scale, $X, [System.IO.Path]::GetFileName($out))
}
Write-Host '(first run can take a while; many / large images longer)'
Write-Host ''

# Run from the tool's own directory so it finds its bundled models/ folder
# (the ncnn build has no -m flag and resolves models relative to the cwd/exe).
$exeDir = [System.IO.Path]::GetDirectoryName($exe)
Push-Location $exeDir
try {
    & (".\" + $ExeName) -i "$Path" -o "$out" -n $model -s $scale -f png
}
finally { Pop-Location }

Write-Host ''
if ($isFolder) {
    # Judge by produced files, not exit code — a stray non-image in the folder
    # can make the tool exit non-zero even though every image was upscaled.
    $made = if (Test-Path -LiteralPath $out) {
        @(Get-ChildItem -LiteralPath $out -File -ErrorAction SilentlyContinue).Count
    } else { 0 }
    if ($made -gt 0) {
        Write-Host ("Done -> {0}  ({1} of {2} image(s))" -f $out, $made, $imgs.Count)
    }
    else {
        Write-Host "Upscale failed (exit $LASTEXITCODE). No Vulkan GPU? Real-ESRGAN can't run."
    }
}
elseif ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $out)) {
    Write-Host ("Done -> " + $out)
}
else {
    Write-Host "Upscale failed (exit $LASTEXITCODE). No Vulkan GPU? Real-ESRGAN can't run."
}
PauseExit
