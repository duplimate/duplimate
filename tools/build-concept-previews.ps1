# Generates 30+ candidate icon concepts as 256x256 PNGs plus a contact
# sheet for visual comparison. Used once, to let the user pick a final
# design. After a concept is chosen, copy its drawing logic into
# tools/build-icon.ps1 and delete this script (or keep for history).
#
# Output:
#   assets/brand/concepts/<slug>.png           one per concept
#   assets/brand/concepts/_contact-sheet.png   grid

[CmdletBinding()]
param(
    [string] $Accent = '#0061FF',    # Ocean blue background
    [string] $Fg     = '#FBFAF6',    # near-white marks
    [int]    $Size   = 256
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $repo 'assets\brand\concepts'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

function HexToColor([string]$hex) {
    $h = $hex.TrimStart('#')
    return [System.Drawing.Color]::FromArgb(0xFF,
        [Convert]::ToInt32($h.Substring(0,2),16),
        [Convert]::ToInt32($h.Substring(2,2),16),
        [Convert]::ToInt32($h.Substring(4,2),16))
}

$bgColor = HexToColor $Accent
$fgColor = HexToColor $Fg
$bgBrush = New-Object System.Drawing.SolidBrush($bgColor)
$fgBrush = New-Object System.Drawing.SolidBrush($fgColor)

# ---- primitives ------------------------------------------------------

function RoundRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x,       $y,       $d, $d, 180, 90) | Out-Null
    $p.AddArc($x+$w-$d, $y,       $d, $d, 270, 90) | Out-Null
    $p.AddArc($x+$w-$d, $y+$h-$d, $d, $d,   0, 90) | Out-Null
    $p.AddArc($x,       $y+$h-$d, $d, $d,  90, 90) | Out-Null
    $p.CloseFigure()
    return $p
}
function FillRR($g, $x, $y, $w, $h, $r, $br) { $p = RoundRectPath $x $y $w $h $r; $g.FillPath($br, $p); $p.Dispose() }
function FillCircle($g, $cx, $cy, $r, $br) { $g.FillEllipse($br, $cx-$r, $cy-$r, $r*2, $r*2) }
function StrokeCircle($g, $cx, $cy, $r, $pen) { $g.DrawEllipse($pen, $cx-$r, $cy-$r, $r*2, $r*2) }
function StrokeArc($g, $cx, $cy, $r, $startA, $sweepA, $pen) {
    $g.DrawArc($pen, $cx-$r, $cy-$r, $r*2, $r*2, $startA, $sweepA)
}
function StrokeLine($g, $x1, $y1, $x2, $y2, $pen) { $g.DrawLine($pen, $x1, $y1, $x2, $y2) }

# Polygon helper — pass coordinates as a flat list of doubles (x,y,x,y,...)
# to dodge PowerShell's array-of-arrays flattening gotchas.
function FillPoly($g, $br, [double[]] $coords) {
    $arr = New-Object 'System.Collections.Generic.List[System.Drawing.PointF]'
    for ($i = 0; $i -lt $coords.Count; $i += 2) {
        $arr.Add((New-Object System.Drawing.PointF([float]$coords[$i], [float]$coords[$i+1])))
    }
    $g.FillPolygon($br, $arr.ToArray())
}

function NewPen($color, [float]$thickness) {
    $p = New-Object System.Drawing.Pen($color, $thickness)
    $p.StartCap = 'Round'; $p.EndCap = 'Round'; $p.LineJoin = 'Round'
    return $p
}

# Shared "friendly cloud" shape: five overlapping circles of varied sizes
# (bigger main bump top-center, asymmetric shoulders so it doesn't feel
# geometric) plus a rounded base that meets them softly. All five cloud
# variants use this so the family is consistent and the extras only
# differ by what's drawn inside or nearby.
function Write-FriendlyCloud($g, $s, $brush) {
    FillCircle $g ($s*0.50) ($s*0.40) ($s*0.18) $brush   # main bump (top-center)
    FillCircle $g ($s*0.31) ($s*0.48) ($s*0.13) $brush   # upper-left shoulder
    FillCircle $g ($s*0.68) ($s*0.44) ($s*0.15) $brush   # upper-right bump
    FillCircle $g ($s*0.79) ($s*0.55) ($s*0.10) $brush   # lower-right rounding
    FillCircle $g ($s*0.22) ($s*0.58) ($s*0.10) $brush   # lower-left rounding
    FillRR $g ($s*0.17) ($s*0.52) ($s*0.66) ($s*0.18) ($s*0.09) $brush   # soft base
}

# ---- concept catalogue -----------------------------------------------
# Each entry: slug -> scriptblock that draws the shape in a $s-square
# coordinate space. $g = Graphics, $s = size in px. The tile background
# (rounded-square, accent-colored) is drawn by the dispatcher before
# calling the concept block, so each block only draws the foreground.

$concepts = [ordered]@{

    # ============ STORAGE / ARCHIVE =================================
    'ocean-stack' = {
        param($g, $s)
        FillRR $g ($s*0.188) ($s*0.305) ($s*0.547) ($s*0.109) ($s*0.023) $fgBrush
        FillRR $g ($s*0.188) ($s*0.445) ($s*0.625) ($s*0.109) ($s*0.023) $fgBrush
        FillRR $g ($s*0.188) ($s*0.586) ($s*0.508) ($s*0.109) ($s*0.023) $fgBrush
    }
    'box-stack' = {
        param($g, $s)
        FillRR $g ($s*0.24) ($s*0.20) ($s*0.50) ($s*0.20) ($s*0.04) $fgBrush
        FillRR $g ($s*0.20) ($s*0.44) ($s*0.56) ($s*0.20) ($s*0.04) $fgBrush
        FillRR $g ($s*0.28) ($s*0.66) ($s*0.44) ($s*0.16) ($s*0.04) $fgBrush
    }
    'folder-stack' = {
        param($g, $s)
        FillPoly $g $fgBrush @(
            ($s*0.17), ($s*0.40),
            ($s*0.35), ($s*0.40),
            ($s*0.42), ($s*0.48),
            ($s*0.83), ($s*0.48),
            ($s*0.83), ($s*0.80),
            ($s*0.17), ($s*0.80)
        )
        FillPoly $g $fgBrush @(
            ($s*0.22), ($s*0.24),
            ($s*0.38), ($s*0.24),
            ($s*0.44), ($s*0.31),
            ($s*0.80), ($s*0.31),
            ($s*0.80), ($s*0.38),
            ($s*0.22), ($s*0.38)
        )
    }
    'archive-drawer' = {
        param($g, $s)
        FillRR $g ($s*0.2) ($s*0.2) ($s*0.6) ($s*0.6) ($s*0.04) $fgBrush
        FillRR $g ($s*0.27) ($s*0.27) ($s*0.46) ($s*0.12) ($s*0.02) $bgBrush
        FillRR $g ($s*0.27) ($s*0.44) ($s*0.46) ($s*0.12) ($s*0.02) $bgBrush
        FillRR $g ($s*0.27) ($s*0.61) ($s*0.46) ($s*0.12) ($s*0.02) $bgBrush
        FillRR $g ($s*0.44) ($s*0.32) ($s*0.12) ($s*0.02) ($s*0.01) $fgBrush
        FillRR $g ($s*0.44) ($s*0.49) ($s*0.12) ($s*0.02) ($s*0.01) $fgBrush
        FillRR $g ($s*0.44) ($s*0.66) ($s*0.12) ($s*0.02) ($s*0.01) $fgBrush
    }
    'database-cylinder' = {
        param($g, $s)
        FillRR $g ($s*0.25) ($s*0.22) ($s*0.50) ($s*0.56) ($s*0.04) $fgBrush
        $g.FillEllipse($fgBrush, [float]($s*0.25), [float]($s*0.18), [float]($s*0.50), [float]($s*0.14))
        $g.FillEllipse($bgBrush, [float]($s*0.25), [float]($s*0.22), [float]($s*0.50), [float]($s*0.10))
        $g.FillEllipse($bgBrush, [float]($s*0.25), [float]($s*0.40), [float]($s*0.50), [float]($s*0.08))
        $g.FillEllipse($bgBrush, [float]($s*0.25), [float]($s*0.55), [float]($s*0.50), [float]($s*0.08))
        $g.FillEllipse($fgBrush, [float]($s*0.25), [float]($s*0.70), [float]($s*0.50), [float]($s*0.14))
    }
    'hard-drive' = {
        param($g, $s)
        FillRR $g ($s*0.20) ($s*0.30) ($s*0.60) ($s*0.40) ($s*0.05) $fgBrush
        FillCircle $g ($s*0.62) ($s*0.50) ($s*0.08) $bgBrush
        FillCircle $g ($s*0.62) ($s*0.50) ($s*0.03) $fgBrush
        FillRR $g ($s*0.26) ($s*0.62) ($s*0.16) ($s*0.025) ($s*0.01) $bgBrush
    }
    'tape-reel' = {
        param($g, $s)
        FillCircle $g ($s*0.33) ($s*0.5) ($s*0.16) $fgBrush
        FillCircle $g ($s*0.67) ($s*0.5) ($s*0.16) $fgBrush
        FillCircle $g ($s*0.33) ($s*0.5) ($s*0.06) $bgBrush
        FillCircle $g ($s*0.67) ($s*0.5) ($s*0.06) $bgBrush
        $pen = NewPen $fgColor ($s*0.03)
        StrokeLine $g ($s*0.33) ($s*0.34) ($s*0.67) ($s*0.34) $pen
        $pen.Dispose()
    }

    # ============ PROTECTION / SAFEKEEPING ==========================
    'shield' = {
        param($g, $s)
        FillPoly $g $fgBrush @(
            ($s*0.50), ($s*0.18),
            ($s*0.78), ($s*0.28),
            ($s*0.78), ($s*0.52),
            ($s*0.50), ($s*0.82),
            ($s*0.22), ($s*0.52),
            ($s*0.22), ($s*0.28)
        )
    }
    'shield-check' = {
        param($g, $s)
        FillPoly $g $fgBrush @(
            ($s*0.50), ($s*0.18),
            ($s*0.78), ($s*0.28),
            ($s*0.78), ($s*0.52),
            ($s*0.50), ($s*0.82),
            ($s*0.22), ($s*0.52),
            ($s*0.22), ($s*0.28)
        )
        $pen = NewPen $bgColor ($s*0.06)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddLine([float]($s*0.35),[float]($s*0.50),[float]($s*0.46),[float]($s*0.62)) | Out-Null
        $path.AddLine([float]($s*0.46),[float]($s*0.62),[float]($s*0.67),[float]($s*0.40)) | Out-Null
        $g.DrawPath($pen, $path)
        $pen.Dispose(); $path.Dispose()
    }
    'vault' = {
        param($g, $s)
        FillCircle $g ($s*0.5) ($s*0.5) ($s*0.34) $fgBrush
        FillCircle $g ($s*0.5) ($s*0.5) ($s*0.26) $bgBrush
        FillCircle $g ($s*0.5) ($s*0.5) ($s*0.05) $fgBrush
        foreach ($a in 0,45,90,135) {
            $rad = $a * [Math]::PI / 180
            $pen = NewPen $fgColor ($s*0.03)
            StrokeLine $g ($s*0.5 + [Math]::Cos($rad)*$s*0.05) ($s*0.5 + [Math]::Sin($rad)*$s*0.05) `
                           ($s*0.5 + [Math]::Cos($rad)*$s*0.30) ($s*0.5 + [Math]::Sin($rad)*$s*0.30) $pen
            $pen.Dispose()
        }
    }
    'padlock' = {
        param($g, $s)
        $pen = NewPen $fgColor ($s*0.07)
        StrokeArc $g ($s*0.5) ($s*0.42) ($s*0.14) 180 180 $pen
        $pen.Dispose()
        FillRR $g ($s*0.30) ($s*0.42) ($s*0.40) ($s*0.36) ($s*0.05) $fgBrush
        FillCircle $g ($s*0.5) ($s*0.56) ($s*0.045) $bgBrush
        FillRR $g ($s*0.485) ($s*0.56) ($s*0.03) ($s*0.12) ($s*0.01) $bgBrush
    }
    'key-shield' = {
        param($g, $s)
        FillPoly $g $fgBrush @(
            ($s*0.50), ($s*0.18),
            ($s*0.78), ($s*0.28),
            ($s*0.78), ($s*0.52),
            ($s*0.50), ($s*0.82),
            ($s*0.22), ($s*0.52),
            ($s*0.22), ($s*0.28)
        )
        FillCircle $g ($s*0.5) ($s*0.44) ($s*0.07) $bgBrush
        FillPoly $g $bgBrush @(
            ($s*0.46), ($s*0.44),
            ($s*0.54), ($s*0.44),
            ($s*0.52), ($s*0.62),
            ($s*0.48), ($s*0.62)
        )
    }
    'umbrella' = {
        param($g, $s)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddPie([float]($s*0.14),[float]($s*0.24),[float]($s*0.72),[float]($s*0.56),180,180) | Out-Null
        $g.FillPath($fgBrush, $path)
        $path.Dispose()
        FillRR $g ($s*0.485) ($s*0.52) ($s*0.03) ($s*0.26) ($s*0.01) $fgBrush
        $pen = NewPen $fgColor ($s*0.03)
        StrokeArc $g ($s*0.44) ($s*0.76) ($s*0.055) 0 180 $pen
        $pen.Dispose()
    }
    'anchor' = {
        param($g, $s)
        $pen = NewPen $fgColor ($s*0.05)
        StrokeCircle $g ($s*0.5) ($s*0.24) ($s*0.06) $pen
        StrokeLine $g ($s*0.5) ($s*0.30) ($s*0.5) ($s*0.72) $pen
        StrokeLine $g ($s*0.38) ($s*0.40) ($s*0.62) ($s*0.40) $pen
        StrokeArc $g ($s*0.5) ($s*0.62) ($s*0.20) 10 160 $pen
        $pen.Dispose()
    }
    'fortress' = {
        param($g, $s)
        FillPoly $g $fgBrush @(
            ($s*0.20), ($s*0.40),
            ($s*0.30), ($s*0.40),
            ($s*0.30), ($s*0.30),
            ($s*0.42), ($s*0.30),
            ($s*0.42), ($s*0.40),
            ($s*0.58), ($s*0.40),
            ($s*0.58), ($s*0.30),
            ($s*0.70), ($s*0.30),
            ($s*0.70), ($s*0.40),
            ($s*0.80), ($s*0.40),
            ($s*0.80), ($s*0.78),
            ($s*0.20), ($s*0.78)
        )
        FillRR $g ($s*0.44) ($s*0.54) ($s*0.12) ($s*0.24) ($s*0.06) $bgBrush
    }

    # ============ FLIGHT / AIRLINE ==================================
    'paper-plane' = {
        param($g, $s)
        FillPoly $g $fgBrush @(
            ($s*0.18), ($s*0.50),
            ($s*0.82), ($s*0.24),
            ($s*0.56), ($s*0.56),
            ($s*0.68), ($s*0.76),
            ($s*0.46), ($s*0.64)
        )
        FillPoly $g $bgBrush @(
            ($s*0.18), ($s*0.50),
            ($s*0.46), ($s*0.64),
            ($s*0.56), ($s*0.56)
        )
    }
    'airliner' = {
        param($g, $s)
        FillRR $g ($s*0.47) ($s*0.16) ($s*0.06) ($s*0.68) ($s*0.03) $fgBrush
        $g.FillEllipse($fgBrush, [float]($s*0.45), [float]($s*0.12), [float]($s*0.10), [float]($s*0.10))
        FillPoly $g $fgBrush @(
            ($s*0.14), ($s*0.58),
            ($s*0.86), ($s*0.58),
            ($s*0.62), ($s*0.46),
            ($s*0.38), ($s*0.46)
        )
        FillPoly $g $fgBrush @(
            ($s*0.34), ($s*0.80),
            ($s*0.66), ($s*0.80),
            ($s*0.56), ($s*0.72),
            ($s*0.44), ($s*0.72)
        )
    }
    'wings' = {
        param($g, $s)
        FillPoly $g $fgBrush @(
            ($s*0.50), ($s*0.40),
            ($s*0.16), ($s*0.58),
            ($s*0.36), ($s*0.56),
            ($s*0.50), ($s*0.52)
        )
        FillPoly $g $fgBrush @(
            ($s*0.50), ($s*0.40),
            ($s*0.84), ($s*0.58),
            ($s*0.64), ($s*0.56),
            ($s*0.50), ($s*0.52)
        )
        FillCircle $g ($s*0.5) ($s*0.46) ($s*0.09) $fgBrush
        FillCircle $g ($s*0.5) ($s*0.46) ($s*0.04) $bgBrush
    }
    'paper-plane-trail' = {
        param($g, $s)
        FillPoly $g $fgBrush @(
            ($s*0.38), ($s*0.44),
            ($s*0.84), ($s*0.22),
            ($s*0.62), ($s*0.50),
            ($s*0.72), ($s*0.66),
            ($s*0.56), ($s*0.56)
        )
        FillCircle $g ($s*0.18) ($s*0.76) ($s*0.04) $fgBrush
        FillCircle $g ($s*0.24) ($s*0.70) ($s*0.035) $fgBrush
        FillCircle $g ($s*0.30) ($s*0.62) ($s*0.03) $fgBrush
        FillCircle $g ($s*0.36) ($s*0.54) ($s*0.025) $fgBrush
    }
    'cloud-plane' = {
        param($g, $s)
        # Friendly cloud, slightly nudged down-left to make room for a paper-plane
        # popping out the upper right. Draw cloud in a saved transform.
        $state = $g.Save()
        $g.TranslateTransform([float]($s*-0.03), [float]($s*0.12))
        $g.ScaleTransform(0.88, 0.88)
        Write-FriendlyCloud $g $s $fgBrush
        $g.Restore($state)
        FillPoly $g $fgBrush @(
            ($s*0.58), ($s*0.32),
            ($s*0.86), ($s*0.20),
            ($s*0.70), ($s*0.34),
            ($s*0.74), ($s*0.44),
            ($s*0.64), ($s*0.38)
        )
    }

    # ============ CLOUD / UPLOAD ====================================
    'cloud' = {
        param($g, $s)
        Write-FriendlyCloud $g $s $fgBrush
    }
    'cloud-up' = {
        # friendlier cloud + upward arrow BELOW the cloud
        param($g, $s)
        $state = $g.Save()
        $g.TranslateTransform(0, [float]($s*-0.08))
        $g.ScaleTransform(0.85, 0.85)
        # re-center after scale
        $g.TranslateTransform([float]($s*0.09), [float]($s*0.02))
        Write-FriendlyCloud $g $s $fgBrush
        $g.Restore($state)
        # arrow, centered, below the cloud body
        FillPoly $g $fgBrush @(
            ($s*0.50), ($s*0.60),
            ($s*0.36), ($s*0.72),
            ($s*0.44), ($s*0.72),
            ($s*0.44), ($s*0.84),
            ($s*0.56), ($s*0.84),
            ($s*0.56), ($s*0.72),
            ($s*0.64), ($s*0.72)
        )
    }
    'cloud-arrow-in' = {
        # friendlier cloud with an upward arrow cut out of its body (bg color)
        param($g, $s)
        Write-FriendlyCloud $g $s $fgBrush
        # arrow in negative space — sized to fit inside the cloud footprint
        # body (vertical bar)
        FillRR $g ($s*0.455) ($s*0.48) ($s*0.09) ($s*0.20) ($s*0.02) $bgBrush
        # arrowhead (triangle wider than the bar)
        FillPoly $g $bgBrush @(
            ($s*0.50), ($s*0.36),
            ($s*0.36), ($s*0.50),
            ($s*0.64), ($s*0.50)
        )
        # tuck the bar-top under the arrowhead corners (small fill)
        FillRR $g ($s*0.455) ($s*0.485) ($s*0.09) ($s*0.03) ($s*0.01) $bgBrush
    }
    'cloud-check' = {
        param($g, $s)
        Write-FriendlyCloud $g $s $fgBrush
        # check in negative space inside the cloud
        $pen = NewPen $bgColor ($s*0.05)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddLine([float]($s*0.37),[float]($s*0.50),[float]($s*0.47),[float]($s*0.60)) | Out-Null
        $path.AddLine([float]($s*0.47),[float]($s*0.60),[float]($s*0.65),[float]($s*0.42)) | Out-Null
        $g.DrawPath($pen, $path)
        $pen.Dispose(); $path.Dispose()
    }

    # ============ TIME / HISTORY ====================================
    'clock-back' = {
        param($g, $s)
        $pen = NewPen $fgColor ($s*0.06)
        StrokeArc $g ($s*0.5) ($s*0.5) ($s*0.30) 30 330 $pen
        FillPoly $g $fgBrush @(
            ($s*0.78), ($s*0.30),
            ($s*0.88), ($s*0.38),
            ($s*0.74), ($s*0.42)
        )
        StrokeLine $g ($s*0.5) ($s*0.5) ($s*0.5) ($s*0.34) $pen
        StrokeLine $g ($s*0.5) ($s*0.5) ($s*0.62) ($s*0.5) $pen
        $pen.Dispose()
    }
    'hourglass' = {
        param($g, $s)
        FillPoly $g $fgBrush @(
            ($s*0.26), ($s*0.20),
            ($s*0.74), ($s*0.20),
            ($s*0.50), ($s*0.50)
        )
        FillPoly $g $fgBrush @(
            ($s*0.50), ($s*0.50),
            ($s*0.26), ($s*0.80),
            ($s*0.74), ($s*0.80)
        )
        FillRR $g ($s*0.22) ($s*0.16) ($s*0.56) ($s*0.04) ($s*0.015) $fgBrush
        FillRR $g ($s*0.22) ($s*0.80) ($s*0.56) ($s*0.04) ($s*0.015) $fgBrush
    }
    'tree-rings' = {
        param($g, $s)
        $pen = NewPen $fgColor ($s*0.04)
        StrokeCircle $g ($s*0.5) ($s*0.5) ($s*0.32) $pen
        StrokeCircle $g ($s*0.5) ($s*0.5) ($s*0.23) $pen
        StrokeCircle $g ($s*0.5) ($s*0.5) ($s*0.14) $pen
        FillCircle $g ($s*0.5) ($s*0.5) ($s*0.05) $fgBrush
        $pen.Dispose()
    }

    # ============ ABSTRACT / GEOMETRIC ==============================
    'chevrons' = {
        param($g, $s)
        $pen = NewPen $fgColor ($s*0.07)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        foreach ($y in 0.30, 0.46, 0.62) {
            $path.StartFigure()
            $path.AddLine([float]($s*0.25),[float]($s*($y+0.10)),[float]($s*0.50),[float]($s*$y)) | Out-Null
            $path.AddLine([float]($s*0.50),[float]($s*$y),    [float]($s*0.75),[float]($s*($y+0.10))) | Out-Null
        }
        $g.DrawPath($pen, $path)
        $pen.Dispose(); $path.Dispose()
    }
    'capsule-bars' = {
        param($g, $s)
        FillRR $g ($s*0.14) ($s*0.38) ($s*0.72) ($s*0.24) ($s*0.12) $fgBrush
        FillRR $g ($s*0.26) ($s*0.44) ($s*0.10) ($s*0.12) ($s*0.02) $bgBrush
        FillRR $g ($s*0.45) ($s*0.44) ($s*0.10) ($s*0.12) ($s*0.02) $bgBrush
        FillRR $g ($s*0.64) ($s*0.44) ($s*0.10) ($s*0.12) ($s*0.02) $bgBrush
    }
    'target' = {
        param($g, $s)
        $pen = NewPen $fgColor ($s*0.05)
        StrokeCircle $g ($s*0.5) ($s*0.5) ($s*0.30) $pen
        StrokeCircle $g ($s*0.5) ($s*0.5) ($s*0.20) $pen
        FillCircle $g ($s*0.5) ($s*0.5) ($s*0.09) $fgBrush
        $pen.Dispose()
    }
    'orbit' = {
        param($g, $s)
        FillCircle $g ($s*0.5) ($s*0.5) ($s*0.12) $fgBrush
        $pen = NewPen $fgColor ($s*0.04)
        $state = $g.Save()
        $g.TranslateTransform([float]($s*0.5), [float]($s*0.5))
        $g.RotateTransform(-25)
        $g.DrawEllipse($pen, [float]($s*-0.36), [float]($s*-0.18), [float]($s*0.72), [float]($s*0.36))
        $g.Restore($state)
        FillCircle $g ($s*0.82) ($s*0.34) ($s*0.05) $fgBrush
        $pen.Dispose()
    }
    'pyramid-layers' = {
        param($g, $s)
        FillPoly $g $fgBrush @(
            ($s*0.46), ($s*0.22),
            ($s*0.54), ($s*0.22),
            ($s*0.60), ($s*0.36),
            ($s*0.40), ($s*0.36)
        )
        FillPoly $g $fgBrush @(
            ($s*0.38), ($s*0.38),
            ($s*0.62), ($s*0.38),
            ($s*0.68), ($s*0.54),
            ($s*0.32), ($s*0.54)
        )
        FillPoly $g $fgBrush @(
            ($s*0.30), ($s*0.56),
            ($s*0.70), ($s*0.56),
            ($s*0.78), ($s*0.76),
            ($s*0.22), ($s*0.76)
        )
    }
    'iso-cube' = {
        param($g, $s)
        $mid = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(230, $fgColor))
        FillPoly $g $fgBrush @(
            ($s*0.5),  ($s*0.20),
            ($s*0.80), ($s*0.35),
            ($s*0.5),  ($s*0.50),
            ($s*0.20), ($s*0.35)
        )
        $dim = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(180, $fgColor))
        FillPoly $g $dim @(
            ($s*0.20), ($s*0.35),
            ($s*0.5),  ($s*0.50),
            ($s*0.5),  ($s*0.82),
            ($s*0.20), ($s*0.67)
        )
        $dim2 = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(140, $fgColor))
        FillPoly $g $dim2 @(
            ($s*0.80), ($s*0.35),
            ($s*0.5),  ($s*0.50),
            ($s*0.5),  ($s*0.82),
            ($s*0.80), ($s*0.67)
        )
        $mid.Dispose(); $dim.Dispose(); $dim2.Dispose()
    }
    'snapshot-camera' = {
        param($g, $s)
        FillRR $g ($s*0.48) ($s*0.24) ($s*0.14) ($s*0.06) ($s*0.01) $fgBrush
        FillRR $g ($s*0.18) ($s*0.30) ($s*0.64) ($s*0.44) ($s*0.05) $fgBrush
        FillCircle $g ($s*0.5) ($s*0.52) ($s*0.14) $bgBrush
        FillCircle $g ($s*0.5) ($s*0.52) ($s*0.09) $fgBrush
    }
    'parachute' = {
        param($g, $s)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddPie([float]($s*0.12),[float]($s*0.16),[float]($s*0.76),[float]($s*0.60),180,180) | Out-Null
        $g.FillPath($fgBrush, $path)
        $path.Dispose()
        $pen = NewPen $bgColor ($s*0.02)
        foreach ($x in 0.28, 0.50, 0.72) {
            StrokeLine $g ($s*$x) ($s*0.16) ($s*$x) ($s*0.46) $pen
        }
        $pen.Dispose()
        $pen2 = NewPen $fgColor ($s*0.015)
        StrokeLine $g ($s*0.16) ($s*0.46) ($s*0.42) ($s*0.68) $pen2
        StrokeLine $g ($s*0.84) ($s*0.46) ($s*0.58) ($s*0.68) $pen2
        $pen2.Dispose()
        FillRR $g ($s*0.40) ($s*0.66) ($s*0.20) ($s*0.14) ($s*0.02) $fgBrush
    }
    'bolt' = {
        param($g, $s)
        FillPoly $g $fgBrush @(
            ($s*0.52), ($s*0.16),
            ($s*0.28), ($s*0.54),
            ($s*0.46), ($s*0.54),
            ($s*0.38), ($s*0.84),
            ($s*0.70), ($s*0.44),
            ($s*0.52), ($s*0.44),
            ($s*0.62), ($s*0.16)
        )
    }
}

# ---- render each concept + build contact sheet -----------------------

function Write-ConceptIcon([string]$slug, [scriptblock]$draw) {
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'

    $tp = RoundRectPath 0 0 $Size $Size ($Size * 0.18)
    $g.FillPath($bgBrush, $tp); $tp.Dispose()

    & $draw $g $Size

    $outPath = Join-Path $outDir ("{0}.png" -f $slug)
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Output ("  " + $slug)
}

Write-Output ("Rendering {0} concepts at {1}x{1}px..." -f $concepts.Count, $Size)
foreach ($k in $concepts.Keys) {
    Write-ConceptIcon $k $concepts[$k]
}

# ---- contact sheet ---------------------------------------------------

$cols = 6
$rows = [Math]::Ceiling($concepts.Count / $cols)
$cellW = 180; $cellH = 210
$pad = 20
$sheetW = $cols * $cellW + $pad * 2
$sheetH = $rows * $cellH + $pad * 2

$sheet = New-Object System.Drawing.Bitmap($sheetW, $sheetH)
$sg = [System.Drawing.Graphics]::FromImage($sheet)
$sg.SmoothingMode = 'AntiAlias'
$sg.Clear([System.Drawing.Color]::FromArgb(255, 249, 247, 243))

$font = New-Object System.Drawing.Font('Segoe UI', 10, [System.Drawing.FontStyle]::Regular)
$brushLabel = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 95, 95, 95))

$i = 0
foreach ($k in $concepts.Keys) {
    $c = $i % $cols
    $r = [Math]::Floor($i / $cols)
    $x = $pad + $c * $cellW + ($cellW - 160) / 2
    $y = $pad + $r * $cellH + 10

    $icon = [System.Drawing.Image]::FromFile((Join-Path $outDir ("{0}.png" -f $k)))
    $sg.DrawImage($icon, $x, $y, 160, 160)
    $icon.Dispose()

    $lfmt = New-Object System.Drawing.StringFormat
    $lfmt.Alignment = 'Center'
    $lrect = New-Object System.Drawing.RectangleF([float]($pad + $c*$cellW), [float]($y + 168), [float]$cellW, 30)
    $sg.DrawString($k, $font, $brushLabel, $lrect, $lfmt)

    $i++
}

$sheetPath = Join-Path $outDir '_contact-sheet.png'
$sheet.Save($sheetPath, [System.Drawing.Imaging.ImageFormat]::Png)
$sg.Dispose(); $sheet.Dispose()
$font.Dispose(); $brushLabel.Dispose()

Write-Output ''
Write-Output ("Contact sheet: " + $sheetPath)
Write-Output ("Individual PNGs in: " + $outDir)
