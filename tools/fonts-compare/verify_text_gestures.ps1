# Verifies the four things: edit box, I-beam, rotated editing, drag selection.
$ErrorActionPreference = 'Stop'
function Op($op, $params) {
    $b = @{ op = $op; params = $params } | ConvertTo-Json -Depth 10
    (Invoke-RestMethod -Uri 'http://127.0.0.1:5099/api/v1/invoke' -Method Post `
        -ContentType 'application/json' -Body $b -TimeoutSec 300)
}
function Shot($name) {
    $r = Invoke-RestMethod -Uri 'http://127.0.0.1:5099/api/v1/screenshot' -TimeoutSec 120
    [System.IO.File]::WriteAllBytes("C:\Users\submu\vccad-win\artifacts\$name.png",
        [Convert]::FromBase64String($r.result.pngBase64))
}

Op 'document.new' @{ name = 'Text gestures' } | Out-Null
Start-Sleep -Milliseconds 600
$board = (Op 'artboard.list' @{}).result[0]
Op 'artboard.setBounds' @{ artboardId = $board.artboardId; x = 0; y = 0;
                           width = 595.276; height = 841.89 } | Out-Null
Op 'view.fit' @{} | Out-Null
Start-Sleep -Milliseconds 700

$zoom = (Op 'view.status' @{}).result.zoom
$view = (Op 'view.center' @{}).result
function Px([double]$x, [double]$y) {
    @{ x = [int](520 + ($x - $view.x) * $zoom); y = [int](520 + ($y - $view.y) * $zoom) }
}

Write-Host "1. draw a text box, 200pt wide"
Op 'tool.set' @{ tool = 'text' } | Out-Null
$a = Px 60 120; $b = Px 260 120
Op 'input.pointer' @{ action = 'press';   x = $a.x; y = $a.y } | Out-Null
Start-Sleep -Milliseconds 200
Op 'input.pointer' @{ action = 'release'; x = $b.x; y = $b.y } | Out-Null
Start-Sleep -Milliseconds 300

Write-Host "2. type three lines - box should grow downwards, width fixed"
Op 'input.type' @{ text = "The quick brown fox jumps over the lazy dog and keeps on running past the gate." } | Out-Null
Start-Sleep -Milliseconds 900
$c = Op 'text.caret' @{}
Write-Host "   caret: $($c.result | ConvertTo-Json -Compress)"
Shot 'box-1'

Write-Host "3. drag-select across the first line"
$s1 = Px 60 124
$s2 = Px 200 124
Op 'input.pointer' @{ action = 'press';   x = $s1.x; y = $s1.y } | Out-Null
Start-Sleep -Milliseconds 200
for ($i = 1; $i -le 6; $i++) {
    $mx = [int]($s1.x + ($s2.x - $s1.x) * $i / 6)
    Op 'input.pointer' @{ action = 'move'; x = $mx; y = $s1.y; leftDown = $true } | Out-Null
    Start-Sleep -Milliseconds 80
}
Op 'input.pointer' @{ action = 'release'; x = $s2.x; y = $s2.y } | Out-Null
Start-Sleep -Milliseconds 400
$sel = Op 'text.caret' @{}
Write-Host "   after drag: $($sel.result | ConvertTo-Json -Compress)"
Shot 'box-2'

Write-Host "4. rotate the block 20 degrees, then click its middle"
Op 'ui.keys' @{ keys = 'Escape' } | Out-Null
Start-Sleep -Milliseconds 300
Op 'text.style' @{ rotationDegrees = 20 } | Out-Null
Start-Sleep -Milliseconds 400
Op 'tool.set' @{ tool = 'select' } | Out-Null
Start-Sleep -Milliseconds 200
$mid = Px 142.7 185.2
Op 'input.pointer' @{ clickCount = 2; x = $mid.x; y = $mid.y } | Out-Null
Start-Sleep -Milliseconds 700
$rot = Op 'text.caret' @{}
Write-Host "   after double-click on rotated text: $($rot.result | ConvertTo-Json -Compress)"
Shot 'box-3'
Write-Host "done"
