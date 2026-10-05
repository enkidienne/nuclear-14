# Regenerates the power armor RSIs from raw 32x48 source art: <piece>-<dir>.png for
# frame/helmet/chest/lefthand/righthand/leftleg/rightleg across n/e/s/w. The source art is
# not kept in the repo; point -Source at a folder of it to regenerate.
#
# $ShiftY lifts the art off the canvas floor: Robust centres sprite layers on the entity
# origin, so feet on row 47 of 48 float ~8px below this repo's 32x32 mob ground line
# (t45.rsi/equipped-OUTERCLOTHING has feet on row 31 of 32). Row 39 of 48 matches it.

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

# Load a source piece, shifted vertically, as a fresh 32x48 ARGB bitmap.
function Get-Piece([string]$name, [string]$dir) {
    $path = Join-Path $Source "$name-$dir.png"
    if (-not (Test-Path -LiteralPath $path)) { throw "missing source: $path" }

    $src = New-Object System.Drawing.Bitmap((Resolve-Path -LiteralPath $path).Path)
    try {
        $out = New-Sheet $TileW $TileH
        $g = [System.Drawing.Graphics]::FromImage($out)
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
        # negative Y pulls the art up so the feet land on the mob ground line
        $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, $ShiftY, $TileW, $TileH),
                              (New-Object System.Drawing.Rectangle 0, 0, $TileW, $TileH),
                              [System.Drawing.GraphicsUnit]::Pixel)
        $g.Dispose()
        return $out
    } finally {
        $src.Dispose()
    }
}

function New-Dir4([string]$name) {
    $sheet = New-Sheet ($TileW * 2) ($TileH * 2)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    for ($i = 0; $i -lt 4; $i++) {
        $piece = Get-Piece $name $DirOrder[$i]
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

function New-Single([string]$name) {
    # icon / inhand art reuses the south-facing render
    return (Get-Piece $name 's')
}

# Build one RSI directory: 4 directional worn states + icon + inhand per piece.
function Build-Rsi([string]$outDir, [string[]]$pieces) {
    if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null

    $states = New-Object System.Collections.Generic.List[object]

    foreach ($piece in $pieces) {
        # RsiLoading resolves each state's image as exactly "<stateId>.png" (RSIResource
        # LoadPreTextureFolder), so a "-0" frame suffix makes the whole RSI fail to load.
        $dirSheet = "$outDir/$piece.png"
        (New-Dir4 $piece).Save($dirSheet, [System.Drawing.Imaging.ImageFormat]::Png)
        $states.Add([ordered]@{ name = $piece; directions = 4 })

        $states.Add([ordered]@{ name = "$piece-icon" })
        (New-Single $piece).Save("$outDir/$piece-icon.png", [System.Drawing.Imaging.ImageFormat]::Png)

        foreach ($hand in @('left', 'right')) {
            $stateName = "$piece-inhand-$hand"
            $states.Add([ordered]@{ name = $stateName })
            (New-Single $piece).Save("$outDir/$stateName.png", [System.Drawing.Imaging.ImageFormat]::Png)
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
    # Must be UTF-8 *without* a BOM; one (as PS 5.1's Set-Content emits) makes the RSI fail to load.
    [System.IO.File]::WriteAllText("$outDir/meta.json", $json, (New-Object System.Text.UTF8Encoding($false)))

    Write-Host "$outDir -> $($states.Count) states"
}

$root = Join-Path (Get-Location) "Resources\Textures\_Nuclear14\Clothing"
Build-Rsi (Join-Path $root "PowerArmorFrame\frame.rsi")    $FramePieces
Build-Rsi (Join-Path $root "PowerArmorPieces\pieces.rsi")  $SlotPieces

Write-Host "done"