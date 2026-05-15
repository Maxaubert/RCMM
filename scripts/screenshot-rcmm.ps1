# Captures the RCMM window (or any titled window matching a substring) to a PNG.
# Usage: .\screenshot-rcmm.ps1 [-OutFile <path>] [-WindowTitle <substring>]
param(
  [string]$OutFile = "$env:TEMP\rcmm-screenshot.png",
  [string]$WindowTitle = "RCMM"
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
  [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
  [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);
  public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
  public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
  public const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_SHOWWINDOW = 0x0040;
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@

Add-Type -AssemblyName System.Drawing

# Find a top-level window whose title contains $WindowTitle (case-insensitive).
$hits = New-Object System.Collections.ArrayList
$cb = [W+EnumWindowsProc]{
  param($hWnd, $lParam)
  if (-not [W]::IsWindowVisible($hWnd)) { return $true }
  $sb = New-Object System.Text.StringBuilder 512
  [W]::GetWindowText($hWnd, $sb, 512) | Out-Null
  $title = $sb.ToString()
  if ($title -and $title.ToLower().Contains($WindowTitle.ToLower())) {
    $hits.Add(@{ HWnd = $hWnd; Title = $title }) | Out-Null
  }
  return $true
}
[W]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null

if ($hits.Count -eq 0) { Write-Error "no window with title containing '$WindowTitle'"; exit 2 }
# Prefer the one whose title starts with the substring.
$win = $hits | Where-Object { $_.Title.ToLower().StartsWith($WindowTitle.ToLower()) } | Select-Object -First 1
if (-not $win) { $win = $hits[0] }
$hWnd = $win.HWnd
Write-Output "Capturing window: '$($win.Title)' hWnd=$hWnd"

# Use DWM extended frame bounds (so we don't get the invisible resize-padding margin).
$r = New-Object W+RECT
$hr = [W]::DwmGetWindowAttribute($hWnd, 9, [ref]$r, [System.Runtime.InteropServices.Marshal]::SizeOf($r))
if ($hr -ne 0) {
  [W]::GetWindowRect($hWnd, [ref]$r) | Out-Null
}

# Bring to foreground so it isn't occluded. WinUI 3 windows can't be PrintWindow'd
# (DirectComposition); we screen-capture instead, which requires the window to be
# actually visible. Use HWND_TOPMOST + SwitchToThisWindow to bypass Windows' focus-
# stealing prevention, then restore non-topmost after the capture.
[W]::ShowWindow($hWnd, 9) | Out-Null # SW_RESTORE
[W]::SetWindowPos($hWnd, [W]::HWND_TOPMOST, 0, 0, 0, 0, [W]::SWP_NOMOVE -bor [W]::SWP_NOSIZE -bor [W]::SWP_SHOWWINDOW) | Out-Null
[W]::SwitchToThisWindow($hWnd, $true)
Start-Sleep -Milliseconds 500

$w = $r.R - $r.L
$h = $r.B - $r.T
if ($w -le 0 -or $h -le 0) { Write-Error "invalid window rect"; exit 3 }

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
$g.Dispose()
# Restore non-topmost so the window doesn't permanently float.
[W]::SetWindowPos($hWnd, [W]::HWND_NOTOPMOST, 0, 0, 0, 0, [W]::SWP_NOMOVE -bor [W]::SWP_NOSIZE) | Out-Null
$dir = Split-Path $OutFile -Parent
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output ("Saved {0} ({1} x {2})" -f $OutFile, $w, $h)
