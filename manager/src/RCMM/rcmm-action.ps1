# rcmm-action.ps1 — RCMM smart "power actions" launched by right-click verbs:
#   powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden `
#     -File rcmm-action.ps1 -Action <name> -Path "<file|folder>"
#
#   sha256    hash the file -> clipboard (+ tray balloon)
#   unblock   strip Mark-of-the-Web from a downloaded file
#   takeown   take ownership of a file/folder (self-elevates via UAC)
#   adminterm open an elevated terminal in the folder
param(
    # Not [Mandatory]: this runs with -WindowStyle Hidden, so a missing argument
    # would make PowerShell prompt on an invisible console and hang forever. We
    # validate below and surface a balloon instead.
    [ValidateSet('sha256', 'unblock', 'takeown', 'adminterm')][string]$Action,
    [string]$Path
)
$ErrorActionPreference = 'Stop'

function Notify([string]$msg) {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        $ni = New-Object System.Windows.Forms.NotifyIcon
        $ni.Icon = [System.Drawing.SystemIcons]::Information
        $ni.Visible = $true
        $ni.BalloonTipTitle = 'RCMM'
        $ni.BalloonTipText = $msg
        $ni.ShowBalloonTip(3000)
        # Keep the icon alive past the balloon's duration, or it never renders.
        Start-Sleep -Milliseconds 3800
        $ni.Dispose()
    }
    catch {}
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

# Quote one argument for a Windows command line (CommandLineToArgvW rules) so a
# folder name with spaces, ';', '&' etc. arrives as a SINGLE token and cannot
# inject extra arguments — critical here because the target shells run ELEVATED.
# Windows filenames can't contain '"', so the only real hazard is a run of
# trailing backslashes (e.g. a drive root "C:\") escaping the closing quote;
# double those. Empty string quotes to "".
function Quote-Arg([string]$s) {
    if ($s -ne '' -and $s -notmatch '[\s"]') { return $s }
    $s = $s -replace '(\\*)"', '$1$1\"'   # escape any embedded quote (defensive)
    $s = $s -replace '(\\+)$', '$1$1'     # double trailing backslashes
    return '"' + $s + '"'
}

# Well-known SID for the local Administrators group — locale-independent, unlike
# the literal name "administrators" which doesn't exist on non-English Windows.
$AdministratorsSid = '*S-1-5-32-544'

if ([string]::IsNullOrWhiteSpace($Action) -or [string]::IsNullOrWhiteSpace($Path)) {
    Notify 'RCMM action was launched without a valid target.'
    exit 1
}

try {
    switch ($Action) {
        'sha256' {
            $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
            Set-Clipboard -Value $hash
            Notify ("SHA-256 copied to clipboard:`n" + $hash)
        }
        'unblock' {
            Unblock-File -LiteralPath $Path
            Notify ("Unblocked: " + [System.IO.Path]::GetFileName($Path))
        }
        'takeown' {
            if (-not (Test-Admin)) {
                # Re-launch this same script elevated (UAC), then exit the non-admin copy.
                Start-Process powershell -Verb RunAs -WindowStyle Hidden -ArgumentList @(
                    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
                    '-File', (Quote-Arg $PSCommandPath), '-Action', 'takeown', '-Path', (Quote-Arg $Path))
                return
            }
            # Grant to the Administrators SID, not the literal group name, so this
            # works on non-English Windows. Capture native exit codes (the pipe to
            # Out-Null doesn't disturb $LASTEXITCODE) and only claim success if the
            # grant actually applied.
            if (Test-Path -LiteralPath $Path -PathType Container) {
                takeown /f "$Path" /r /d Y | Out-Null
                icacls "$Path" /grant "${AdministratorsSid}:F" /t /c | Out-Null
            }
            else {
                takeown /f "$Path" | Out-Null
                icacls "$Path" /grant "${AdministratorsSid}:F" | Out-Null
            }
            if ($LASTEXITCODE -eq 0) {
                Notify ("Took ownership of: " + [System.IO.Path]::GetFileName($Path))
            }
            else {
                Notify ("Couldn't take ownership of '" + [System.IO.Path]::GetFileName($Path) + "' (icacls exit $LASTEXITCODE).")
            }
        }
        'adminterm' {
            if (Get-Command wt -ErrorAction SilentlyContinue) {
                # Quote the directory so a folder name with spaces/';' can't inject
                # extra wt subcommands into the ELEVATED terminal.
                Start-Process wt -Verb RunAs -ArgumentList @('-d', (Quote-Arg $Path))
            }
            else {
                # Double every apostrophe so $Path can't break out of the single-quoted
                # string. Inside a single-quoted PowerShell literal that is the ONLY
                # metacharacter, so this fully neutralizes a folder name crafted to
                # inject commands into this ELEVATED shell (e.g. a folder named  '; calc; ').
                $safe = $Path.Replace("'", "''")
                Start-Process powershell -Verb RunAs -ArgumentList @(
                    '-NoExit', '-NoProfile', '-Command', ("Set-Location -LiteralPath '" + $safe + "'"))
            }
        }
    }
}
catch {
    # Don't fail silently — surface the reason (incl. a declined UAC prompt).
    Notify ("RCMM action failed:`n" + $_.Exception.Message)
}
