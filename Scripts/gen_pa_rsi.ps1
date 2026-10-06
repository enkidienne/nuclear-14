# Regenerates the power armor RSIs from raw 32x48 source art: <piece>-<dir>.png across
# n/e/s/w. The source art is not kept in the repo; point -Source at a folder of it.
#
# $ShiftY lifts the art so the feet land on this repo's 32x32 mob ground line:
# row 39 of 48 matches feet on row 31 of 32.

param(
    [string]$Source = ".\PA",
    [string]$License = "CC-BY-NC-SA-3.0",
    [string]$Copyright = "TODO: replace with artist attribution"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$TileW = 32
$TileH = 48
$ShiftY = -8

# RSI frames are read row-major in the order South, North, East, West: S,N top row, E,W below.
$DirOrder = @('s', 'n', 'e', 'w')

$FramePieces = @('frame')
$SlotPieces  = @('helmet', 'chest', 'lefthand', 'righthand', 'leftleg', 'rightleg')

function New-Sheet([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bmp.SetResolution(96, 96)
    return $bmp
}

# Where the committed RSIs live, as the fallback art source.$TextureRoot = Join-Path (Get-Location) "Resources\Textures\_Nuclear14\Clothing"

# Reads one piece tile, falling back to the committed sheet when the raw art is gone.
function Get-Piece([string]$name, [string]$dir, [string]$existingRsiDir) {
    $path = Join-Path $Source "$name-$dir.png"

    if (Test-Path -LiteralPath $path) {
        $src = New-Object System.Drawing.Bitmap((Resolve-Path -LiteralPath $path).Path)
        try {
            $out = New-Sheet $TileW $TileH
            $g = [System.Drawing.Graphics]::FromImage($out)
            $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
            # negative Y lifts the art so the feet land on the mob ground line
            $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, $ShiftY, $TileW, $TileH),
                                  (New-Object System.Drawing.Rectangle 0, 0, $TileW, $TileH),
                                  [System.Drawing.GraphicsUnit]::Pixel)
            $g.Dispose()
            return $out
        } finally {
            $src.Dispose()
        }
    }

    # Already-shifted art from the sheet; Dir4 tiles read S,N,E,W row-major.
    $sheetPath = Join-Path $existingRsiDir "$name.png"
    if (-not (Test-Path -LiteralPath $sheetPath)) { throw "missing source: $path and $sheetPath" }

    $index = [Array]::IndexOf($DirOrder, $dir)
    $sheet = New-Object System.Drawing.Bitmap((Resolve-Path -LiteralPath $sheetPath).Path)
    try {
        $out = New-Sheet $TileW $TileH
        $g = [System.Drawing.Graphics]::FromImage($out)
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $srcRect = New-Object System.Drawing.Rectangle (($index % 2) * $TileW), ([Math]::Floor($index / 2) * $TileH), $TileW, $TileH
        $g.DrawImage($sheet, (New-Object System.Drawing.Rectangle 0, 0, $TileW, $TileH), $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
        $g.Dispose()
        return $out
    } finally {
        $sheet.Dispose()
    }
}

function New-Dir4([string]$name, [string]$existingRsiDir) {
    $sheet = New-Sheet ($TileW * 2) ($TileH * 2)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    for ($i = 0; $i -lt 4; $i++) {
        $piece = Get-Piece $name $DirOrder[$i] $existingRsiDir
        $col = $i % 2
        $row = [Math]::Floor($i / 2)
        $g.DrawImage($piece, (New-Object System.Drawing.Rectangle ($col * $TileW), ($row * $TileH), $TileW, $TileH),
                               (New-Object System.Drawing.Rectangle 0, 0, $TileW, $TileH),
                               [System.Drawing.GraphicsUnit]::Pixel)
        $piece.Dispose()
    }
    $g.Dispose()
    return $sheet
}

function New-Single([string]$name, [string]$existingRsiDir) {
    # icon / inhand reuse the south-facing render
    return (Get-Piece $name 's' $existingRsiDir)
}


# 4 directional states plus icon and inhand per piece.
function Build-Rsi([string]$outDir, [string[]]$pieces) {
    # Snapshot the sheets before wiping: they are the fallback art source.
    $snapDir = $null
    if (Test-Path -LiteralPath $outDir) {
        $snapDir = Join-Path ([System.IO.Path]::GetTempPath()) ("pa-src-" + [System.IO.Path]::GetRandomFileName())
        New-Item -ItemType Directory -Path $snapDir -Force | Out-Null
        foreach ($p in $pieces) {
            $sheet = Join-Path $outDir "$p.png"
            if (Test-Path -LiteralPath $sheet) {
                Copy-Item -LiteralPath $sheet -Destination (Join-Path $snapDir "$p.png") -Force
            }
        }
    }

    if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null

    # Raw source if present, else the snapshot.
    $artDir = if (Test-Path -LiteralPath (Join-Path $Source "$($pieces[0]).png")) { $Source } else { $snapDir }

    $states = New-Object System.Collections.Generic.List[object]

    foreach ($piece in $pieces) {
        # RsiLoading resolves each image as exactly "<stateId>.png", so no "-0" suffix.
        $dirSheet = "$outDir/$piece.png"
        (New-Dir4 $piece $artDir).Save($dirSheet, [System.Drawing.Imaging.ImageFormat]::Png)
        $states.Add([ordered]@{ name = $piece; directions = 4 })

        $states.Add([ordered]@{ name = "$piece-icon" })
        (New-Single $piece $artDir).Save("$outDir/$piece-icon.png", [System.Drawing.Imaging.ImageFormat]::Png)

        foreach ($hand in @('left', 'right')) {
            $stateName = "$piece-inhand-$hand"
            $states.Add([ordered]@{ name = $stateName })
            (New-Single $piece $artDir).Save("$outDir/$stateName.png", [System.Drawing.Imaging.ImageFormat]::Png)
        }
    }

    $meta = [ordered]@{
        version  = 1
        size     = [ordered]@{ x = $TileW; y = $TileH }
        license  = $License
        copyright = $Copyright
        states   = $states
    }

    $json = $meta | ConvertTo-Json -Depth 6
    # Must be UTF-8 *without* a BOM or the RSI fails to load.
    [System.IO.File]::WriteAllText("$outDir/meta.json", $json, (New-Object System.Text.UTF8Encoding($false)))

    Write-Host "$outDir -> $($states.Count) states"
}

$root = Join-Path (Get-Location) "Resources\Textures\_Nuclear14\Clothing"
Build-Rsi (Join-Path $root "PowerArmorFrame\frame.rsi")    $FramePieces
Build-Rsi (Join-Path $root "PowerArmorPieces\pieces.rsi")  $SlotPieces

Write-Host "done"
