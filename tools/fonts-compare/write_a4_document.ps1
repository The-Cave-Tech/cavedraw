# A4 lorem ipsum, written entirely through injected pointer and keyboard events.
$ErrorActionPreference = 'Stop'

function Op($op, $params) {
    $b = @{ op = $op; params = $params } | ConvertTo-Json -Depth 10
    (Invoke-RestMethod -Uri 'http://127.0.0.1:5099/api/v1/invoke' -Method Post `
        -ContentType 'application/json' -Body $b -TimeoutSec 300)
}

Op 'document.new' @{ name = 'A4 Lorem Ipsum' } | Out-Null
Start-Sleep -Milliseconds 500
$board = (Op 'artboard.list' @{}).result[0]
Op 'artboard.setBounds' @{ artboardId = $board.artboardId; x = 0; y = 0;
                           width = 595.276; height = 841.89 } | Out-Null
Op 'view.fit' @{} | Out-Null
Start-Sleep -Milliseconds 600

$zoom = (Op 'view.status' @{}).result.zoom
$view = (Op 'view.center' @{}).result
$CX = 520; $CY = 520
function Px([double]$x, [double]$y) {
    @{ x = [int]($CX + ($x - $view.x) * $zoom); y = [int]($CY + ($y - $view.y) * $zoom) }
}

# --- helpers ---------------------------------------------------------------
function NewFrame([double]$x, [double]$y, [double]$w, [double]$h) {
    Op 'tool.set' @{ tool = 'text' } | Out-Null
    $a = Px $x $y
    $b = Px ($x + $w) ($y + $h)
    Op 'input.pointer' @{ action = 'press';   x = $a.x; y = $a.y } | Out-Null
    Start-Sleep -Milliseconds 200
    Op 'input.pointer' @{ action = 'release'; x = $b.x; y = $b.y } | Out-Null
    Start-Sleep -Milliseconds 200
}

function TypeIt([string]$text) {
    Op 'input.type' @{ text = $text } | Out-Null
    Start-Sleep -Milliseconds 350
}

function Done() {
    Op 'tool.set' @{ tool = 'select' } | Out-Null
    Start-Sleep -Milliseconds 200
}

function Face([string]$family, [double]$size, [bool]$bold, [bool]$italic, $rgb) {
    Op 'text.update' @{ family = $family; fontSize = $size; bold = $bold; italic = $italic;
                        color = $rgb } | Out-Null
    Start-Sleep -Milliseconds 200
}

function Para($lineSpacing, $paragraphSpacing, $rotation, $align) {
    $p = @{}
    if ($lineSpacing)     { $p.lineSpacing = $lineSpacing }
    if ($paragraphSpacing) { $p.paragraphSpacing = $paragraphSpacing }
    if ($rotation)        { $p.rotationDegrees = $rotation }
    if ($align)           { $p.alignment = $align }
    if ($p.Count -gt 0) { Op 'text.style' $p | Out-Null }
    Start-Sleep -Milliseconds 200
}

$p1 = "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor " +
      "incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud " +
      "exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat."
$p2 = "Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu " +
      "fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa " +
      "qui officia deserunt mollit anim id est laborum."

# --- masthead --------------------------------------------------------------
Write-Host "masthead"
NewFrame 55 55 480 30
TypeIt "De Finibus Bonorum et Malorum"
Done; Face 'Nimbus Roman' 28 $true $false @(18, 26, 56)

Write-Host "standfirst"
NewFrame 55 92 480 18
TypeIt "Section I - the standard placeholder text of the printing trade"
Done; Face 'Nimbus Sans' 11 $false $true @(110, 110, 110)

Write-Host "rule caption"
NewFrame 55 118 480 12
TypeIt "REF 3464 / LILLIE - A4 - 210 x 297 mm - 150 dpi"
Done; Face 'Nimbus Mono PS' 8 $false $false @(120, 120, 120)

# --- two columns of body copy ---------------------------------------------
Write-Host "body column 1"
NewFrame 55 160 228 150
TypeIt ($p1 + "`n" + $p2)
Done; Face 'Nimbus Roman' 10 $false $false @(24, 24, 24)
Para 1.45 8 $null $null

Write-Host "body column 2"
NewFrame 312 160 228 150
TypeIt ($p2 + "`n" + $p1)
Done; Face 'Nimbus Sans' 9.5 $false $false @(24, 24, 24)
Para 1.35 10 $null $null

# --- pull quote, rotated ---------------------------------------------------
Write-Host "pull quote (rotated)"
NewFrame 55 360 420 40
TypeIt "Neque porro quisquam est qui dolorem ipsum quia dolor sit amet"
Done; Face 'Nimbus Roman' 17 $true $true @(150, 40, 40)
Para 1.2 0 -6 'center'

# --- hanging section --------------------------------------------------------
Write-Host "section"
NewFrame 55 470 480 100
TypeIt ("Section II - On the Ends of Good and Evil`n" +
        "At vero eos et accusamus et iusto odio dignissimos ducimus qui blanditiis " +
        "praesentium voluptatum deleniti atque corrupti quos dolores et quas molestias " +
        "excepturi sint occaecati cupiditate non provident.")
Done; Face 'Nimbus Roman' 10 $false $false @(30, 60, 40)
Para 1.4 6 $null 'left'

# --- footer -----------------------------------------------------------------
Write-Host "footer"
NewFrame 55 780 480 12
TypeIt "Typeset in Nimbus Roman, Nimbus Sans and Nimbus Mono"
Done; Face 'Nimbus Sans' 8 $false $false @(140, 140, 140)

Op 'selection.clear' @{} | Out-Null
Op 'view.fit' @{} | Out-Null
Start-Sleep -Milliseconds 400
$texts = (Op 'object.find' @{ type = 'text' }).result
Write-Host "text objects: $($texts.count)"
