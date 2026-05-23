# rcmm-action.ps1 — RCMM smart "power actions" launched by right-click verbs:
#   powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden `
#     -File rcmm-action.ps1 -Action <name> -Path "<file|folder>"
#
#   sha256    hash the file -> clipboard (+ tray balloon)
#   unblock   strip Mark-of-the-Web from a downloaded file
#   takeown   take ownership of a file/folder (self-elevates via UAC)
#   adminterm open an elevated terminal in the folder
param(
    [Parameter(Mandatory = $true)][ValidateSet('sha256', 'unblock', 'takeown', 'adminterm')][string]$Action,
    [Parameter(Mandatory = $true)][string]$Path
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
                    '-File', "`"$PSCommandPath`"", '-Action', 'takeown', '-Path', "`"$Path`"")
                return
            }
            if (Test-Path -LiteralPath $Path -PathType Container) {
                takeown /f "$Path" /r /d Y | Out-Null
                icacls "$Path" /grant "administrators:F" /t /c | Out-Null
            }
            else {
                takeown /f "$Path" | Out-Null
                icacls "$Path" /grant "administrators:F" | Out-Null
            }
            Notify ("Took ownership of: " + [System.IO.Path]::GetFileName($Path))
        }
        'adminterm' {
            if (Get-Command wt -ErrorAction SilentlyContinue) {
                Start-Process wt -Verb RunAs -ArgumentList @('-d', $Path)
            }
            else {
                Start-Process powershell -Verb RunAs -ArgumentList @(
                    '-NoExit', '-NoProfile', '-Command', ("Set-Location -LiteralPath '" + $Path + "'"))
            }
        }
    }
}
catch {
    # Don't fail silently — surface the reason (incl. a declined UAC prompt).
    Notify ("RCMM action failed:`n" + $_.Exception.Message)
}
