# rcmm-compress.ps1 — RCMM "Compress" smart action (video, for now).
#
# Launched by the right-click verb:
#   powershell -NoProfile -ExecutionPolicy Bypass -File rcmm-compress.ps1 "<file>"
#
# Probes the source with ffprobe (codec / resolution / bitrate / duration),
# then walks the user through four boxed arrow-key pickers — Codec, Quality,
# Resolution, Audio — each with a "Keep as is" row on top. Re-encodes with
# ffmpeg using CRF (constant quality), NOT a bitrate/size target: this tool
# fixes quality and lets the codec decide the size. The size-target sibling
# ("Compress to size", 2-pass) is a separate, future template — see ROADMAP.md.
#
# Quality is mapped to a per-codec CRF (H.265 ~20/26/30, H.264 ~18/23/28,
# AV1 ~25/32/40); resolution is a percentage of the *probed* dimensions so it
# works for any aspect ratio (rounded to even numbers, which the codecs need).
#
# Needs a VT-capable terminal for the lime highlight (Windows Terminal /
# modern conhost on Win10 1809+ / Win11). ffmpeg+ffprobe ship together in the
# Gyan.FFmpeg winget package; the dependency check offers to install it.
#
# -DryRun probes + prints a representative ffmpeg invocation and exits (used to
# verify probing and argument-building without sitting through an encode).
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Path,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# Render Unicode (box-drawing, the ▸ marker, the × in resolutions) as real
# glyphs instead of "?" / best-fit mojibake from a legacy console codepage.
try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch {}

function PauseExit {
    Write-Host ''
    Read-Host 'Press Enter to close' | Out-Null
    exit
}

# Boxed, arrow-key navigable column menu. Rows are '|'-delimited strings (one
# cell per column) — a flat string[] sidesteps PowerShell's array-of-arrays
# flattening. Returns the chosen row index, or -1 on Esc. The header row is
# shown only when -HideHeader is absent (column labels matter for the codec
# table; the other pickers are self-explanatory).
function Show-ColumnMenu {
    param(
        [string]$Title,
        [string]$Status,
        [string]$Headers,    # '|'-delimited column labels
        [string[]]$Rows,     # each a '|'-delimited row
        [switch]$HideHeader
    )

    $E     = [char]27
    $lime  = $E + '[38;2;212;255;58m'
    $dim   = $E + '[38;2;138;138;147m'
    $text  = $E + '[38;2;241;241;243m'
    $reset = $E + '[0m'

    $TL = [char]0x250C; $TR = [char]0x2510; $BL = [char]0x2514; $BR = [char]0x2518
    $VL = [char]0x251C; $VR = [char]0x2524; $V = [char]0x2502
    $arrow = [char]0x25B8

    $hdr  = $Headers -split '\|'
    $ncol = $hdr.Count

    # Column widths = max of header (unless hidden) and every cell in that column.
    $cw = New-Object 'int[]' $ncol
    for ($c = 0; $c -lt $ncol; $c++) { $cw[$c] = if ($HideHeader) { 0 } else { $hdr[$c].Length } }
    foreach ($r in $Rows) {
        $cells = $r -split '\|'
        for ($c = 0; $c -lt $ncol; $c++) {
            $l = ([string]$cells[$c]).Length
            if ($l -gt $cw[$c]) { $cw[$c] = $l }
        }
    }

    # Every data/header line is a 3-char prefix (" ▸ " or "   ") + columns
    # joined by two spaces, so column widths are constant across rows.
    $colsLen = 0; foreach ($w in $cw) { $colsLen += $w }
    $colsLen += 2 * ($ncol - 1)
    $rowLineLen = 3 + $colsLen

    $width = 30
    $statusLen = if ($Status) { $Status.Length } else { 0 }
    foreach ($len in @((1 + $Title.Length), (1 + $statusLen), $rowLineLen)) {
        if (($len + 1) -gt $width) { $width = $len + 1 }
    }
    $H = ([string][char]0x2500) * $width

    # Format one row's plain (uncolored) text given its 3-char prefix.
    function Format-Row([string]$prefix, $cells) {
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.Append($prefix)
        for ($c = 0; $c -lt $ncol; $c++) {
            if ($c -gt 0) { [void]$sb.Append('  ') }
            [void]$sb.Append(([string]$cells[$c]).PadRight($cw[$c]))
        }
        return $sb.ToString()
    }

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
            if (-not $HideHeader) {
                $plain = Format-Row "   " $hdr
                $lines.Add("  " + $V + $dim + $plain.PadRight($width) + $reset + $V)
            }
            for ($i = 0; $i -lt $Rows.Count; $i++) {
                $cells  = $Rows[$i] -split '\|'
                $prefix = if ($i -eq $sel) { " " + $arrow + " " } else { "   " }
                $plain  = Format-Row $prefix $cells
                $color  = if ($i -eq $sel) { $lime } else { $text }
                $lines.Add("  " + $V + $color + $plain.PadRight($width) + $reset + $V)
            }
            $lines.Add("  " + $BL + $H + $BR)
            [Console]::Out.Write(($lines -join [Environment]::NewLine) + [Environment]::NewLine)

            $k = [Console]::ReadKey($true)
            switch ($k.Key) {
                'UpArrow'   { $sel = ($sel - 1 + $Rows.Count) % $Rows.Count }
                'DownArrow' { $sel = ($sel + 1) % $Rows.Count }
                'Enter'     { return $sel }
                'Escape'    { return -1 }
            }
        }
    }
    finally { try { [Console]::CursorVisible = $true } catch {} }
}

# Resolve a tool by PATH first, then known winget install locations.
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

# ffprobe codec_name -> friendly label for the Source: line.
function Friendly-Codec([string]$c) {
    switch ($c) {
        'h264'        { 'H.264' }
        'hevc'        { 'H.265' }
        'av1'         { 'AV1' }
        'vp9'         { 'VP9' }
        'vp8'         { 'VP8' }
        'mpeg4'       { 'MPEG-4' }
        'mpeg2video'  { 'MPEG-2' }
        'wmv3'        { 'WMV' }
        'vc1'         { 'VC-1' }
        default       { if ($c) { $c.ToUpperInvariant() } else { 'unknown' } }
    }
}

# Source codec_name -> the ffmpeg encoder we'd use to re-encode it (when the
# user keeps the codec but changes quality/resolution). Falls back to H.264.
function Source-Encoder([string]$c) {
    switch ($c) {
        'hevc' { 'libx265' }
        'h264' { 'libx264' }
        'av1'  { 'libsvtav1' }
        'vp9'  { 'libvpx-vp9' }
        default { 'libx264' }
    }
}

# Even-rounded dimension at a scale factor (codecs reject odd dimensions).
function Calc-Dim([int]$v, [double]$f) { [int]([math]::Truncate($v * $f / 2.0)) * 2 }

function Format-Duration($seconds) {
    if (-not $seconds) { return $null }
    $ts = [TimeSpan]::FromSeconds([double]$seconds)
    if ($ts.TotalHours -ge 1) { return ('{0}:{1:00}:{2:00}' -f [int]$ts.TotalHours, $ts.Minutes, $ts.Seconds) }
    return ('{0}:{1:00}' -f [int]$ts.TotalMinutes, $ts.Seconds)
}

# Per-encoder CRF for the High / Balanced / Low quality tiers. Same visual
# quality target across codecs — the codec decides how small the file gets.
$crfTable = @{
    'libx264'    = @{ High = 18; Balanced = 23; Low = 28 }
    'libx265'    = @{ High = 20; Balanced = 26; Low = 30 }
    'libsvtav1'  = @{ High = 25; Balanced = 32; Low = 40 }
    'libvpx-vp9' = @{ High = 24; Balanced = 31; Low = 37 }
}

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host "File not found: $Path"
    PauseExit
}

# Video only for now (images / other types reuse this picker later — see
# ROADMAP.md). Gate by extension: ffprobe would happily treat a PNG as a
# single-frame "video", so we can't rely on stream detection alone.
$ext = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()
$videoExt = @('.mp4', '.mkv', '.mov', '.webm', '.avi', '.m4v', '.wmv', '.flv', '.ts', '.m2ts', '.mpg', '.mpeg')
if ($videoExt -notcontains $ext) {
    Write-Host "RCMM can only compress video files for now (got '$ext')."
    PauseExit
}

# 1. Dependency check (+ optional winget install of ffmpeg/ffprobe).
$ffFallbacks = @('%LOCALAPPDATA%\Microsoft\WinGet\Packages\Gyan.FFmpeg*\*\bin\ffmpeg.exe')
Write-Host 'Checking for ffmpeg... ' -NoNewline
$ffmpeg = Resolve-Tool 'ffmpeg' $ffFallbacks
if (-not $ffmpeg) {
    Write-Host 'not installed.'
    $ans = Read-Host 'Install ffmpeg with winget? [Y/N]'
    if ($ans -eq '' -or $ans -match '^[Yy]') {
        if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
            Write-Host "winget isn't available here. Install ffmpeg manually, then re-run."
            PauseExit
        }
        winget install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements
        $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
        $ffmpeg = Resolve-Tool 'ffmpeg' $ffFallbacks
        if (-not $ffmpeg) {
            Write-Host "ffmpeg still isn't available. Close this window and try again once the install finishes."
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

# ffprobe ships next to ffmpeg; fall back to a PATH/winget lookup.
$ffprobe = Join-Path ([System.IO.Path]::GetDirectoryName($ffmpeg)) 'ffprobe.exe'
if (-not (Test-Path -LiteralPath $ffprobe)) {
    $ffprobe = Resolve-Tool 'ffprobe' @('%LOCALAPPDATA%\Microsoft\WinGet\Packages\Gyan.FFmpeg*\*\bin\ffprobe.exe')
}
if (-not $ffprobe) {
    Write-Host "ffprobe (part of ffmpeg) wasn't found. Re-run once the install finishes."
    PauseExit
}

# 2. Probe the source: one ffprobe JSON call for streams + format.
$probeJson = & $ffprobe -v error -show_entries 'stream=codec_type,codec_name,width,height,bit_rate:format=duration,bit_rate' -of json "$Path" 2>$null | Out-String
try { $probe = $probeJson | ConvertFrom-Json } catch { $probe = $null }
$vstream = $null; $astream = $null
if ($probe -and $probe.streams) {
    $vstream = $probe.streams | Where-Object { $_.codec_type -eq 'video' } | Select-Object -First 1
    $astream = $probe.streams | Where-Object { $_.codec_type -eq 'audio' } | Select-Object -First 1
}
if (-not $vstream) {
    Write-Host "Couldn't read a video stream from this file."
    PauseExit
}

$srcCodec = [string]$vstream.codec_name
$vw = [int]$vstream.width
$vh = [int]$vstream.height
$hasAudio = [bool]$astream
$srcEncoder = Source-Encoder $srcCodec

# Bitrate: prefer the video stream's, else the container's.
$brVal = $null
if ($vstream.bit_rate -and "$($vstream.bit_rate)" -ne 'N/A') { $brVal = [double]$vstream.bit_rate }
elseif ($probe.format.bit_rate -and "$($probe.format.bit_rate)" -ne 'N/A') { $brVal = [double]$probe.format.bit_rate }
$brStr = $null
if ($brVal) {
    if ($brVal -ge 1e6) { $brStr = ('{0:0.#} Mbps' -f ($brVal / 1e6)) }
    else { $brStr = ('{0:0} kbps' -f ($brVal / 1e3)) }
}
$durStr = Format-Duration $probe.format.duration

$X = [char]0x00D7   # ×
$srcParts = @((Friendly-Codec $srcCodec), ("{0}{1}{2}" -f $vw, $X, $vh))
if ($brStr)  { $srcParts += $brStr }
if ($durStr) { $srcParts += $durStr }
# " · "-separated (multi-char separators are awkward with -join, so be explicit).
$sourceLine = 'Source: ' + [string]::Join(' ' + [char]0x00B7 + ' ', $srcParts)

$fileName = [System.IO.Path]::GetFileName($Path)

# ---- DryRun: probe + a representative invocation, no interactive pickers. ----
function Build-OutPath([string]$codecChoice) {
    if ($codecChoice -eq 'keep') { $outExt = $ext }
    elseif (@('.mp4', '.mkv', '.mov') -contains $ext) { $outExt = $ext }
    else { $outExt = '.mp4' }   # weird containers (avi/wmv/webm/…) -> universal MP4
    $dir  = [System.IO.Path]::GetDirectoryName($Path)
    $name = [System.IO.Path]::GetFileNameWithoutExtension($Path)
    $cand = Join-Path $dir ($name + ' (compressed)' + $outExt)
    $n = 2
    while (Test-Path -LiteralPath $cand) {
        $cand = Join-Path $dir ($name + ' (compressed ' + $n + ')' + $outExt)
        $n++
    }
    return $cand
}

# Assemble the ffmpeg argument array for a set of choices.
#   codecChoice : keep | h264 | h265 | av1
#   qualityKey  : keep | High | Balanced | Low
#   resFactor   : 0 (keep) | 0.75 | 0.5 | 0.25
#   audioMode   : keep | compress | remove
function Build-Args([string]$codecChoice, [string]$qualityKey, [double]$resFactor, [string]$audioMode, [string]$outPath) {
    $reEncode = ($codecChoice -ne 'keep') -or ($qualityKey -ne 'keep') -or ($resFactor -ne 0)

    if ($reEncode) {
        switch ($codecChoice) {
            'h265'  { $encoder = 'libx265' }
            'h264'  { $encoder = 'libx264' }
            'av1'   { $encoder = 'libsvtav1' }
            default { $encoder = $srcEncoder }   # 'keep' -> source's encoder
        }
        $vArgs = @('-c:v', $encoder)
        if ($qualityKey -ne 'keep') {
            $crf = $crfTable[$encoder][$qualityKey]
            # libvpx-vp9 only honors -crf as constant-quality with -b:v 0.
            if ($encoder -eq 'libvpx-vp9') { $vArgs += @('-b:v', '0') }
            $vArgs += @('-crf', [string]$crf)
        }
        if ($resFactor -ne 0) {
            $vArgs += @('-vf', "scale=trunc(iw*$resFactor/2)*2:trunc(ih*$resFactor/2)*2")
        }
    }
    else {
        $vArgs = @('-c:v', 'copy')
    }

    switch ($audioMode) {
        'compress' { $aArgs = @('-c:a', 'aac', '-b:a', '128k') }
        'remove'   { $aArgs = @('-an') }
        default    { $aArgs = if ($hasAudio) { @('-c:a', 'copy') } else { @() } }
    }

    return @('-y', '-i', $Path) + $vArgs + $aArgs + @($outPath)
}

if ($DryRun) {
    Write-Host $sourceLine
    Write-Host ("Has audio: {0}   Source encoder (for 'keep'): {1}" -f $hasAudio, $srcEncoder)
    $audioMode = if ($hasAudio) { 'keep' } else { 'remove' }
    $sampleOut = Build-OutPath 'h265'
    $sampleArgs = Build-Args 'h265' 'Balanced' 0.5 $audioMode $sampleOut
    Write-Host 'Sample (H.265 / Balanced / 50% / keep audio):'
    Write-Host ("  ffmpeg " + ($sampleArgs -join ' '))
    exit
}

# 3. Picker 1 — Codec (the one table that needs column headers).
Clear-Host
$dash = [char]0x2014   # — placeholder for the "Keep as is" row's info columns
$codecRows = @(
    ('Keep as is|{0}|{0}|{0}' -f $dash),
    'H.265 (recommended)|Great|Smaller|Most devices',
    'H.264|Good|Small|Plays everywhere',
    'AV1|Best|Smallest|Newer devices'
)
$sel = Show-ColumnMenu -Title ("Compress  " + $fileName) -Status $sourceLine `
                       -Headers 'Codec|Quality|Size|Compatibility' -Rows $codecRows
if ($sel -lt 0) { Write-Host ''; Write-Host 'Cancelled.'; PauseExit }
$codecChoice = @('keep', 'h265', 'h264', 'av1')[$sel]
$codecLabel  = @('Keep as is', 'H.265', 'H.264', 'AV1')[$sel]

# 4. Picker 2 — Quality (single column, no header needed).
Clear-Host
$qSel = Show-ColumnMenu -Title 'Compress video  ·  quality' -Status ("Codec: " + $codecLabel) `
                        -Headers 'Quality' -Rows @('Keep as is', 'High', 'Balanced', 'Low') -HideHeader
if ($qSel -lt 0) { Write-Host ''; Write-Host 'Cancelled.'; PauseExit }
$qualityKey   = @('keep', 'High', 'Balanced', 'Low')[$qSel]
$qualityLabel = @('Keep as is', 'High', 'Balanced', 'Low')[$qSel]

# 5. Picker 3 — Resolution (percentage + live-calculated pixels).
Clear-Host
$resRows = @(
    ("Keep as is|{0}{1}{2}" -f $vw, $X, $vh),
    ("75%|{0}{1}{2}" -f (Calc-Dim $vw 0.75), $X, (Calc-Dim $vh 0.75)),
    ("50%|{0}{1}{2}" -f (Calc-Dim $vw 0.5),  $X, (Calc-Dim $vh 0.5)),
    ("25%|{0}{1}{2}" -f (Calc-Dim $vw 0.25), $X, (Calc-Dim $vh 0.25))
)
$rSel = Show-ColumnMenu -Title 'Compress video  ·  resolution' `
                        -Status ("Codec: " + $codecLabel + "   Quality: " + $qualityLabel) `
                        -Headers 'Scale|Resolution' -Rows $resRows -HideHeader
if ($rSel -lt 0) { Write-Host ''; Write-Host 'Cancelled.'; PauseExit }
$resFactor = @(0, 0.75, 0.5, 0.25)[$rSel]
$resLabel  = @('Keep as is', '75%', '50%', '25%')[$rSel]

# 6. Picker 4 — Audio (skipped when the file has no audio track).
$audioMode = 'keep'
if ($hasAudio) {
    Clear-Host
    $aSel = Show-ColumnMenu -Title 'Compress video  ·  audio' `
                            -Status ("Codec: " + $codecLabel + "   " + $qualityLabel + "   " + $resLabel) `
                            -Headers 'Audio|Effect' -Rows @(
        'Keep as is|no change to audio',
        'Compress|re-encode to AAC ~128 kbps',
        'Remove audio|strip the audio track') -HideHeader
    if ($aSel -lt 0) { Write-Host ''; Write-Host 'Cancelled.'; PauseExit }
    $audioMode = @('keep', 'compress', 'remove')[$aSel]
}

# Nothing to do if every lever is left untouched.
if ($codecChoice -eq 'keep' -and $qualityKey -eq 'keep' -and $resFactor -eq 0 -and $audioMode -eq 'keep') {
    Write-Host ''
    Write-Host 'Nothing selected to change — every option was left "Keep as is".'
    PauseExit
}

$outPath = Build-OutPath $codecChoice
$ffArgs  = Build-Args $codecChoice $qualityKey $resFactor $audioMode $outPath

Write-Host ''
Write-Host ("Compressing -> {0}" -f [System.IO.Path]::GetFileName($outPath))
Write-Host ''
& $ffmpeg @ffArgs

if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $outPath)) {
    $inSize  = (Get-Item -LiteralPath $Path).Length
    $outSize = (Get-Item -LiteralPath $outPath).Length
    $pct = if ($inSize -gt 0) { [int](100 - ($outSize / $inSize * 100)) } else { 0 }
    Write-Host ''
    Write-Host ("Done -> {0}" -f $outPath)
    Write-Host ("  {0:0.0} MB -> {1:0.0} MB  ({2}% smaller)" -f ($inSize / 1MB), ($outSize / 1MB), $pct)
    Start-Process explorer.exe -ArgumentList ("/select,`"" + $outPath + "`"")
}
else {
    Write-Host ''
    Write-Host "Compression failed (exit $LASTEXITCODE)."
}
PauseExit
