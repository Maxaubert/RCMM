# rcmm-convert.ps1 — RCMM "Convert / Change format" smart action.
#
# Launched by the right-click verb:
#   powershell -NoProfile -ExecutionPolicy Bypass -File rcmm-convert.ps1 "<file>"
#
# Detects the file type, checks for the converter tool (offers a winget install
# if it's missing), shows a numbered format menu, and runs the conversion.
param([Parameter(Mandatory = $true)][string]$Path)

$ErrorActionPreference = 'Stop'

function PauseExit {
    Write-Host ''
    Read-Host 'Press Enter to close' | Out-Null
    exit
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

Write-Host ("Convert:  " + [System.IO.Path]::GetFileName($Path))
Write-Host ''

# 1. Dependency check (+ optional winget install).
Write-Host "Checking for $tool... " -NoNewline
$found = Get-Command $tool -ErrorAction SilentlyContinue
if (-not $found) {
    Write-Host 'not installed.'
    $ans = Read-Host "Install $tool with winget? [Y/n]"
    if ($ans -eq '' -or $ans -match '^[Yy]') {
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

# 2. Format menu.
Write-Host ''
Write-Host 'Choose format:'
for ($i = 0; $i -lt $targets.Count; $i++) {
    Write-Host ("  {0}. {1}" -f ($i + 1), $targets[$i].Label)
}
$choice = Read-Host 'Number'
$index = 0
if (-not [int]::TryParse($choice, [ref]$index) -or $index -lt 1 -or $index -gt $targets.Count) {
    Write-Host 'Invalid choice.'
    PauseExit
}
$target = $targets[$index - 1]

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

if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $out)) {
    Write-Host ''
    Write-Host "Done -> $out"
    Start-Process explorer.exe ('/select,"{0}"' -f $out)
}
else {
    Write-Host ''
    Write-Host "Conversion failed (exit $LASTEXITCODE)."
}
PauseExit
