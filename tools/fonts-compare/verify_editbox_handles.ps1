# Verifies the text edit box: real font metrics, tight bounds, and resize handles.
#
# Aiming uses view.toScreen, which converts through the live canvas. An earlier version of
# this script guessed the viewport centre and put the press 39pt away from the handle,
# which is why the handles looked broken when they were not.
$ErrorActionPreference = 'Stop'
function Op($op, $params) {
    $b = @{ op = $op; params = $params } | ConvertTo-Json -Depth 10
    (Invoke-RestMethod -Uri 'http://127.0.0.1:5099/api/v1/invoke' -Method Post `
        -ContentType 'application/json' -Body $b -TimeoutSec 300)
}
function Shots($name) {
    $r = Invoke-RestMethod -Uri 'http://127.0.0.1:5099/api/v1/screenshot' -TimeoutSec 120
    [System.IO.File]::WriteAllBytes("C:\Users\submu\vccad-win\artifacts\$name.png",
        [Convert]::FromBase64String($r.result.pngBase64))
}
function Aim([double]$modelX, [double]$modelY) {
    $p = (Op 'view.toScreen' @{ x = $modelX; y = $modelY }).result
    @{ x = [int]$p.x; y = [int]$p.y }
}
function Text() { (Op 'object.find' @{ type = 'text' }).result.items[0] }

Op 'document.new' @{ name = 'Edit box' } | Out-Null
Start-Sleep -Milliseconds 800
foreach ($d in ((Op 'document.list' @{}).result | Where-Object { $_.name -eq 'Untitled' })) {
    Op 'document.close' @{ index = $d.index } | Out-Null
    Start-Sleep -Milliseconds 400
}
$board = (Op 'artboard.list' @{}).result[0]
Op 'artboard.setBounds' @{ artboardId = $board.artboardId; x = 0; y = 0
                           width = 595.276; height = 841.89 } | Out-Null
Op 'view.fit' @{} | Out-Null
Start-Sleep -Milliseconds 800

$mine = ((Op 'document.list' @{}).result | Where-Object { $_.name -eq 'Edit box' })[0]
Op 'document.select' @{ index = $mine.index } | Out-Null
Start-Sleep -Milliseconds 600
$summary = (Op 'document.summary' @{}).result
if ($summary.document -ne 'Edit box') { throw "wrong document: $($summary.document)" }
Write-Host "document: $($summary.document)"

Write-Host "draw a 220pt text frame at (80,140)"
Op 'tool.set' @{ tool = 'text' } | Out-Null
$a = Aim 80 140; $b = Aim 300 140
Op 'input.pointer' @{ action = 'press';   x = $a.x; y = $a.y } | Out-Null
Start-Sleep -Milliseconds 200
Op 'input.pointer' @{ action = 'release'; x = $b.x; y = $b.y } | Out-Null
Start-Sleep -Milliseconds 400

Op 'input.type' @{ text = "The quick brown fox jumps over the lazy dog, and then it keeps on running well past the gate." } | Out-Null
Start-Sleep -Milliseconds 1200

$t1 = Text
Write-Host ("before: x={0} y={1} w={2} h={3}" -f [math]::Round($t1.x,2), [math]::Round($t1.y,2), [math]::Round($t1.width,2), [math]::Round($t1.height,2))
Shots 'editbox-1'

Write-Host "`ngrab the right-middle handle and drag 70pt left"
$h = Aim ($t1.x + $t1.width) ($t1.y + $t1.height / 2)
$target = Aim ($t1.x + $t1.width - 70) ($t1.y + $t1.height / 2)
Write-Host "  handle at window ($($h.x),$($h.y)) -> ($($target.x),$($target.y))"
Op 'input.pointer' @{ action = 'press'; x = $h.x; y = $h.y } | Out-Null
Start-Sleep -Milliseconds 200
for ($i = 1; $i -le 8; $i++) {
    Op 'input.pointer' @{ action = 'move'
        x = [int]($h.x + ($target.x - $h.x) * $i / 8)
        y = [int]($h.y + ($target.y - $h.y) * $i / 8); leftDown = $true } | Out-Null
    Start-Sleep -Milliseconds 60
}
Op 'input.pointer' @{ action = 'release'; x = $target.x; y = $target.y } | Out-Null
Start-Sleep -Milliseconds 700

$t2 = Text
Write-Host ("after : x={0} y={1} w={2} h={3}" -f [math]::Round($t2.x,2), [math]::Round($t2.y,2), [math]::Round($t2.width,2), [math]::Round($t2.height,2))
if ([math]::Abs($t2.width - $t1.width) -lt 1) {
    Write-Host "RESULT: handle drag did NOT change the width"
} else {
    Write-Host ("RESULT: width {0} -> {1}" -f [math]::Round($t1.width,2), [math]::Round($t2.width,2))
}
Shots 'editbox-2'
