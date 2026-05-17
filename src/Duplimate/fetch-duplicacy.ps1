# Downloads the latest duplicacy_win_x64_*.exe from gilbertchen/duplicacy
# releases to the given -OutPath. Invoked by FetchDuplicacy.targets.
#
# Intentionally self-contained: no dependencies outside stock Windows PowerShell 5.1.
# Re-runs are no-ops — the MSBuild target skips us when the file exists.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $OutPath
)

$ErrorActionPreference = 'Stop'

# Tls12 is the minimum GitHub accepts; older PS defaults to Ssl3/Tls.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$headers = @{ 'User-Agent' = 'Duplimate-build' }

Write-Host "[fetch-duplicacy] Querying GitHub for latest release..."
$release = Invoke-RestMethod `
    -Uri 'https://api.github.com/repos/gilbertchen/duplicacy/releases/latest' `
    -Headers $headers -TimeoutSec 30

$asset = $release.assets | Where-Object { $_.name -match '^duplicacy_win_x64_.*\.exe$' } | Select-Object -First 1
if (-not $asset) {
    throw "Could not find a duplicacy_win_x64_*.exe asset in the latest release."
}

Write-Host ("[fetch-duplicacy] Downloading {0} ({1:N1} MB)" -f $asset.name, ($asset.size / 1MB))

$dir = Split-Path -Parent $OutPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $OutPath -Headers $headers -TimeoutSec 120

$size = (Get-Item $OutPath).Length
Write-Host ("[fetch-duplicacy] Wrote {0} ({1:N1} MB)" -f $OutPath, ($size / 1MB))
