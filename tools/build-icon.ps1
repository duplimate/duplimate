# Generates Duplimate icon assets from the geometry spec in
# assets/brand/duplimate.svg. Does NOT parse SVG — re-implements
# the exact drawing in System.Drawing so we don't need an SVG runtime.
#
# Current design: "cloud-check" for idle, with a syncing variant that
# shows two curved sync arrows inside the cloud body. The syncing
# variant is rendered in N frames (rotated by N/360° each) so the
# taskbar icon can cycle through them for a light-weight sync
# animation during a backup or restore run.
#
# Default output (Ocean only, single frame):
#   assets/brand/duplimate-<size>.png   (Ocean, idle)
#   src/Duplimate/Assets/Duplimate.ico          (Ocean, idle — the default embedded icon)
#
# With -All:
#   src/Duplimate/Assets/Duplimate-<Accent>.ico             (idle, 5 accents)
#   src/Duplimate/Assets/Duplimate-<Accent>-Syncing-<N>.ico (syncing frame N, 5 accents x 4 frames)
#
# Examples:
#   .\tools\build-icon.ps1                       # default Ocean, idle only
#   .\tools\build-icon.ps1 -All                  # all 5 accents, idle + 4 syncing frames = 25 ICOs

[CmdletBinding()]
param(
    [string] $Accent = '#0061FF',
    [string] $Fg     = '#FBFAF6',
    [switch] $All,
    [int]    $SyncingFrameCount = 4
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Split-Path -Parent $PSScriptRoot
$brandDir = Join-Path $repo 'assets\brand'
$outIcoDir = Join-Path $repo 'src\Duplimate\Assets'

New-Item -ItemType Directory -Path $brandDir -Force | Out-Null
New-Item -ItemType Directory -Path $outIcoDir -Force | Out-Null

function Get-HexColor([string]$hex) {
    $h = $hex.TrimStart('#')
    return [System.Drawing.Color]::FromArgb(0xFF,
        [Convert]::ToInt32($h.Substring(0,2),16),
        [Convert]::ToInt32($h.Substring(2,2),16),
        [Convert]::ToInt32($h.Substring(4,2),16))
}

function Get-RoundRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x,       $y,       $d, $d, 180, 90) | Out-Null
    $p.AddArc($x+$w-$d, $y,       $d, $d, 270, 90) | Out-Null
    $p.AddArc($x+$w-$d, $y+$h-$d, $d, $d,   0, 90) | Out-Null
    $p.AddArc($x,       $y+$h-$d, $d, $d,  90, 90) | Out-Null
    $p.CloseFigure()
    return $p
}

# ---- drawing primitives (same geometry as assets/brand/duplimate.svg) ----

function Write-Tile([System.Drawing.Graphics]$g, [int]$s, [System.Drawing.Brush]$bg) {
    $p = Get-RoundRectPath 0 0 $s $s ($s * 0.18)
    $g.FillPath($bg, $p); $p.Dispose()
}

function Write-CloudBody([System.Drawing.Graphics]$g, [int]$s, [System.Drawing.Brush]$fg) {
    $g.FillEllipse($fg, [float]($s*0.32), [float]($s*0.22), [float]($s*0.36), [float]($s*0.36))  # main bump
    $g.FillEllipse($fg, [float]($s*0.18), [float]($s*0.35), [float]($s*0.26), [float]($s*0.26))  # left shoulder
    $g.FillEllipse($fg, [float]($s*0.53), [float]($s*0.29), [float]($s*0.30), [float]($s*0.30))  # right bump
    $g.FillEllipse($fg, [float]($s*0.69), [float]($s*0.45), [float]($s*0.20), [float]($s*0.20))  # lower-right round
    $g.FillEllipse($fg, [float]($s*0.12), [float]($s*0.48), [float]($s*0.20), [float]($s*0.20))  # lower-left round
    $p = Get-RoundRectPath ([float]($s*0.17)) ([float]($s*0.52)) ([float]($s*0.66)) ([float]($s*0.18)) ([float]($s*0.09))
    $g.FillPath($fg, $p); $p.Dispose()
}

function Write-CheckOverlay([System.Drawing.Graphics]$g, [int]$s, [System.Drawing.Color]$bgColor) {
    $pen = New-Object System.Drawing.Pen($bgColor, [float]($s * 0.075))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddLine([float]($s*0.37),[float]($s*0.50),[float]($s*0.47),[float]($s*0.60)) | Out-Null
    $path.AddLine([float]($s*0.47),[float]($s*0.60),[float]($s*0.65),[float]($s*0.42)) | Out-Null
    $g.DrawPath($pen, $path)
    $pen.Dispose(); $path.Dispose()
}

# Two opposing curved arrows forming a sync / refresh loop, rotated by
# $rotationDeg degrees around the icon center. Drawn in bg color so it
# "cuts" through the cloud face.
function Write-SyncOverlay([System.Drawing.Graphics]$g, [int]$s,
                           [System.Drawing.Brush]$bgBrush, [System.Drawing.Color]$bgColor,
                           [float]$rotationDeg) {
    $cx = [float]($s * 0.50)
    $cy = [float]($s * 0.50)
    $r  = [float]($s * 0.14)
    $th = [float]($s * 0.065)

    $state = $g.Save()
    $g.TranslateTransform($cx, $cy)
    $g.RotateTransform($rotationDeg)
    $g.TranslateTransform(-$cx, -$cy)

    $pen = New-Object System.Drawing.Pen($bgColor, $th)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'

    # Arc 1: upper-left to upper-right (start 200°, sweep 130° → ends 330°)
    # Arc 2: lower-right to lower-left (start 20°,  sweep 130° → ends 150°)
    $g.DrawArc($pen, $cx-$r, $cy-$r, $r*2, $r*2, 200, 130)
    $g.DrawArc($pen, $cx-$r, $cy-$r, $r*2, $r*2,  20, 130)

    $pen.Dispose()

    # Arrowheads at the end of each arc (filled triangles, tangent to circle).
    # System.Drawing uses Y-down, angles clockwise from east. Tangent at
    # angle θ in the sweep direction = (sin θ, -cos θ); outward radial = (cos θ, sin θ).
    function Write-Arrowhead($cx2, $cy2, $r2, $angleDeg, $length, $width, $brush, $graphics) {
        $a = $angleDeg * [Math]::PI / 180.0
        $ex = $cx2 + $r2 * [Math]::Cos($a)
        $ey = $cy2 + $r2 * [Math]::Sin($a)
        $tx =  [Math]::Sin($a); $ty = -[Math]::Cos($a)
        $rx =  [Math]::Cos($a); $ry =  [Math]::Sin($a)
        $tipX = $ex + $length * $tx; $tipY = $ey + $length * $ty
        $blX  = $ex + $width * $rx;  $blY  = $ey + $width * $ry
        $brX  = $ex - $width * $rx;  $brY  = $ey - $width * $ry
        $pts = @(
            (New-Object System.Drawing.PointF([float]$tipX, [float]$tipY)),
            (New-Object System.Drawing.PointF([float]$blX,  [float]$blY)),
            (New-Object System.Drawing.PointF([float]$brX,  [float]$brY))
        )
        $graphics.FillPolygon($brush, [System.Drawing.PointF[]]$pts)
    }

    Write-Arrowhead $cx $cy $r 330 ([float]($s*0.07)) ([float]($s*0.055)) $bgBrush $g
    Write-Arrowhead $cx $cy $r 150 ([float]($s*0.07)) ([float]($s*0.055)) $bgBrush $g

    $g.Restore($state)
}

# ---- ICO packaging ---------------------------------------------------

function New-IcoFromRenders([hashtable]$pngBySize, [string]$outPath) {
    $icoSizes = @(16, 32, 48, 64, 128, 256)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$icoSizes.Count)
    $offset = 6 + ($icoSizes.Count * 16)
    foreach ($sz in $icoSizes) {
        $w = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }
        $data = $pngBySize[$sz]
        $bw.Write([byte]$w); $bw.Write([byte]$w)
        $bw.Write([byte]0);  $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$data.Length)
        $bw.Write([uint32]$offset)
        $offset += $data.Length
    }
    foreach ($sz in $icoSizes) { $bw.Write($pngBySize[$sz]) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($outPath, $ms.ToArray())
    $bw.Dispose(); $ms.Dispose()
}

# ---- high-level: render one ICO (idle or syncing-frame-N) ------------

function Write-IconVariant(
    [string]$accentHex,          # background color
    [string]$outIcoPath,
    [string]$state,              # 'Idle' or 'Syncing'
    [float] $rotationDeg = 0,    # ignored for Idle
    [bool]  $writePngs   = $false
) {
    $bgColor = Get-HexColor $accentHex
    $fgColor = Get-HexColor $Fg
    $bgBrush = New-Object System.Drawing.SolidBrush($bgColor)
    $fgBrush = New-Object System.Drawing.SolidBrush($fgColor)

    $sizes = @(16, 32, 48, 64, 128, 256, 512, 1024)
    $pngBySize = @{}
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($s, $s)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode     = 'AntiAlias'
        $g.InterpolationMode = 'HighQualityBicubic'
        $g.PixelOffsetMode   = 'HighQuality'

        Write-Tile      $g $s $bgBrush
        Write-CloudBody $g $s $fgBrush
        if ($state -eq 'Syncing') {
            Write-SyncOverlay $g $s $bgBrush $bgColor $rotationDeg
        } else {
            Write-CheckOverlay $g $s $bgColor
        }
        $g.Dispose()

        if ($writePngs) {
            $p = Join-Path $brandDir ("duplimate-{0}.png" -f $s)
            $bmp.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
        }

        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngBySize[$s] = $ms.ToArray()
        $ms.Dispose(); $bmp.Dispose()
    }

    $bgBrush.Dispose(); $fgBrush.Dispose()
    New-IcoFromRenders $pngBySize $outIcoPath
    Write-Output ("  {0}" -f (Split-Path -Leaf $outIcoPath))
}

# ---- entry point -----------------------------------------------------

$accentPresets = [ordered]@{
    'Ocean'      = '#0061FF'
    'Emerald'    = '#059669'
    'Plum'       = '#7C3AED'
    'Graphite'   = '#1F2937'
    'Terracotta' = '#C2410C'
}

if ($All) {
    Write-Output ("Building ICOs for all {0} accents + {1} syncing frames each..." -f $accentPresets.Count, $SyncingFrameCount)
    foreach ($name in $accentPresets.Keys) {
        $hex = $accentPresets[$name]
        $writePngs = ($name -eq 'Ocean')

        # Idle
        $idle = Join-Path $outIcoDir ("Duplimate-{0}.ico" -f $name)
        Write-IconVariant -accentHex $hex -outIcoPath $idle -state 'Idle' -writePngs $writePngs

        # Syncing frames
        for ($i = 0; $i -lt $SyncingFrameCount; $i++) {
            $rot = 360.0 / $SyncingFrameCount * $i
            $frame = Join-Path $outIcoDir ("Duplimate-{0}-Syncing-{1}.ico" -f $name, $i)
            Write-IconVariant -accentHex $hex -outIcoPath $frame -state 'Syncing' -rotationDeg $rot
        }

        # The default (suffix-less) ICO baked into the exe points at Ocean idle.
        if ($name -eq 'Ocean') {
            $defaultIco = Join-Path $outIcoDir 'Duplimate.ico'
            Copy-Item -LiteralPath $idle -Destination $defaultIco -Force
        }
    }
    Write-Output ''
    Write-Output ('Default ICO: ' + (Join-Path $outIcoDir 'Duplimate.ico'))
    Write-Output ('Brand PNGs:  ' + $brandDir)
} else {
    # Single-accent idle mode — convenient for iterating on the design.
    $named = 'Custom'
    foreach ($k in $accentPresets.Keys) {
        if ([string]::Equals($accentPresets[$k], $Accent, [StringComparison]::OrdinalIgnoreCase)) { $named = $k; break }
    }
    Write-Output ("Accent: {0} ({1})  idle" -f $Accent, $named)
    $idle = Join-Path $outIcoDir ("Duplimate-{0}.ico" -f $named)
    Write-IconVariant -accentHex $Accent -outIcoPath $idle -state 'Idle' -writePngs $true
    if ($named -eq 'Ocean') {
        Copy-Item -LiteralPath $idle -Destination (Join-Path $outIcoDir 'Duplimate.ico') -Force
    }
}

Write-Output ''
Write-Output 'Done. See docs/icon.md for how to change the design.'
