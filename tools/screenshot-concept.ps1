# Sets Preferences.Concept in config.json, launches Duplimate, waits
# for it to paint, captures the window via PrintWindow with
# RENDERFULLCONTENT, saves to disk, closes the app.
#
# Usage: .\screenshot-concept.ps1 -Concept QuietKeeper -OutPath ./screenshot-a.png

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('QuietKeeper', 'WarmPapers', 'ModernNative')]
    [string] $Concept,

    [Parameter(Mandatory = $true)]
    [string] $OutPath,

    [string] $Theme = 'Light'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot 'src\Duplimate\bin\Debug\net10.0-windows10.0.19041.0\Duplimate.exe'
$configDir = Join-Path $repoRoot 'src\Duplimate\bin\Debug\net10.0-windows10.0.19041.0\Duplimate.config'
$configFile = Join-Path $configDir 'config.json'

# 1. Write a minimal config.json so the app knows which concept to use.
New-Item -ItemType Directory -Path $configDir -Force | Out-Null
$cfg = @{
    schemaVersion = 1
    preferences = @{
        theme = $Theme
        concept = $Concept
        openToMainWindow = $true
        showTrayIcon = $true
    }
    destinations = @()
    backups = @()
    monitoring = @{}
    notifications = @{}
    mail = @{}
    restore = @{}
    migrationAttempted = $true   # skip migration popup
}
$cfg | ConvertTo-Json -Depth 10 | Set-Content -Encoding UTF8 -Path $configFile

# 2. Launch the app (kill any stale instance first)
Get-Process Duplimate -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)

# 3. Wait for the window to appear and paint
$p = $null
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    $p = Get-Process Duplimate -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) { break }
}
if (-not $p -or $p.MainWindowHandle -eq [IntPtr]::Zero) { throw "Window never appeared" }
Start-Sleep -Seconds 3   # let it finish painting content

# 4. PrintWindow
Add-Type @"
using System; using System.Drawing; using System.Runtime.InteropServices;
public class WC {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr d, int f);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Ri, B; }
}
"@ -ReferencedAssemblies System.Drawing

$r = New-Object WC+R
[WC]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.Ri - $r.L; $h = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
[WC]::PrintWindow($p.MainWindowHandle, $g.GetHdc(), 2) | Out-Null
$g.ReleaseHdc(); $g.Dispose()
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

# 5. Close the app
Get-Process Duplimate -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

Write-Output ("Saved $Concept ($Theme) - {0}x{1}" -f $w, $h)
