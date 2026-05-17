# Regenerates the 12 screenshots used on the project website (docs/index.html)
# and in README.md from the WebsiteScreenshotTests Avalonia headless harness.
#
# Usage:  pwsh scripts/regenerate-website-screenshots.ps1
#         (Run from repo root, or anywhere — the script resolves its own paths.)
#
# What it does:
#   1. Runs `dotnet test` against WebsiteScreenshotTests only.
#   2. Copies the 12 PNGs from tests/Duplimate.Tests/artifacts/screenshots/
#      to docs/screenshots/ (where the website references them).
#
# Why headless renders, not a real run:
#   The "backup running" screenshot needs a backup mid-run, which would
#   normally require a multi-GB scratch file to keep the run going long
#   enough to capture. Headless rendering sidesteps that — we set the
#   card view-model's IsRunning + OverallPercent directly, so the
#   layout engine paints the same pixels a real run would produce at
#   that instant, with no flake risk and no scratch file. See
#   WebsiteScreenshotTests.Shot_01_Backups_Running for the seed.
#
# Future maintainer notes:
#   - To change which 12 shots end up on the website, edit $WebsiteShots
#     below and add/rename the matching [AvaloniaFact] in
#     WebsiteScreenshotTests.cs.
#   - All 12 shots are full-window captures (MainWindow with sidebar)
#     except the modal dialogs (Onboarding, editors), which ARE the whole
#     interface while open.

$ErrorActionPreference = 'Stop'

# Resolve repo root (this script lives at <repo>/scripts/).
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Write-Host "[screenshots] repo = $repo"

# 1. Re-render every screenshot.
Write-Host "[screenshots] rendering via WebsiteScreenshotTests..."
Push-Location $repo
try {
    dotnet test 'tests/Duplimate.Tests/Duplimate.Tests.csproj' `
        --filter 'FullyQualifiedName~WebsiteScreenshotTests' `
        -p:SkipStubBuild=true -p:SkipDuplicacyDownload=true `
        --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test exited with code $LASTEXITCODE — see output above"
    }
} finally {
    Pop-Location
}

# 2. Copy the 12 PNGs over. Source filenames already use the website's
#    numeric ordering (01-..12-), so the names match 1:1 and we just
#    enumerate from the source dir.
$src = Join-Path $repo 'tests/Duplimate.Tests/artifacts/screenshots'
$dst = Join-Path $repo 'docs/screenshots'
New-Item -ItemType Directory -Path $dst -Force | Out-Null

$WebsiteShots = @(
    '01-backups-running.png',
    '02-backups-overview.png',
    '03-destinations.png',
    '04-restore-pick.png',
    '05-restore-files.png',
    '06-logs.png',
    '07-settings.png',
    '08-onboarding-sources.png',
    '09-onboarding-destination.png',
    '10-onboarding-schedule.png',
    '11-destination-editor-dropbox.png',
    '11b-destination-editor-local.png',
    '12-backup-editor.png'
)

$copied = 0
foreach ($name in $WebsiteShots) {
    $srcPath = Join-Path $src $name
    $dstPath = Join-Path $dst $name
    if (-not (Test-Path $srcPath)) {
        Write-Warning "missing: $srcPath — skipping"
        continue
    }
    Copy-Item -Path $srcPath -Destination $dstPath -Force
    Write-Host "[screenshots] $name"
    $copied++
}

# 3. Drop stale numbered files in docs/screenshots that didn't get
#    refreshed this round (e.g. when the manifest changes). Keeps
#    the website folder honest.
foreach ($leftover in Get-ChildItem -Path $dst -Filter '*.png' -ErrorAction SilentlyContinue) {
    if ($WebsiteShots -notcontains $leftover.Name) {
        Remove-Item $leftover.FullName -Force
        Write-Host "[screenshots] removed stale: $($leftover.Name)"
    }
}

Write-Host "[screenshots] done. $copied website screenshots in docs/screenshots/."
