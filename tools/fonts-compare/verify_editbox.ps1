# Verifies the text edit box: real font metrics, tight bounds, and resize handles.
#
# Leaves the application as it found it: any stray Untitled documents created by earlier
# automation runs are closed first, so a driver never measures the wrong document. That
# mistake cost two false diagnoses already.
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

# --- set up a known-good document -----------------------------------------
# Create the document first: the last document cannot be closed, so the strays have to
# go after there is something to fall back to.
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
Start-Sleep -Milliseconds 700

# make the new document active and confirm it before measuring anything
$mine = ((Op 'document.list' @{}).result | Where-Object { $_.name -eq 'Edit box' })[0]
Op 'document.select' @{ index = $mine.index } | Out-Null
Start-Sleep -Milliseconds 600
$summary = (Op 'document.summary' @{}).result
if ($summary.document -ne 'Edit box') { throw "wrong document: $($summary.document)" }
Write-Host "document: $($summary.document)"

$zoom = (Op 'view.status' @{}).result.zoom
$view = (Op 'view.center' @{}).result
function Px([double]$x, [double]$y) {
    @{ x = [int](520 + ($x - $view.x) * $zoom); y = [int](520 + ($y - $view.y) * $zoom) }
}

Write-Host "draw a 220pt text frame"
Op 'tool.set' @{ tool = 'text' } | Out-Null
$a = Px 80 140; $b = Px 300 140
Op 'input.pointer' @{ action = 'press';   x = $a.x; y = $a.y } | Out-Null
Start-Sleep -Milliseconds 200
Op 'input.pointer' @{ action = 'release'; x = $b.x; y = $b.y } | Out-Null
Start-Sleep -Milliseconds 400

Write-Host "type three wrapped lines"
Op 'input.type' @{ text = "The quick brown fox jumps over the lazy dog, and then it keeps on running well past the gate." } | Out-Null
Start-Sleep -Milliseconds 1200

$item = (Op 'object.find' @{ type = 'text' }).result.items[0]
Write-Host ("frameWidth={0}  bounds={1}x{2}" -f $item.frameWidth, [math]::Round($item.width,2), [math]::Round($item.height,2))
Shots 'editbox-1'

Write-Host "`nresize: grab the right-middle handle and drag in"
$handleModelX = $item.x + $item.width
$h = Px $handleModelX ($item.y + $item.height / 2)
$target = Px ($handleModelX - 70) ($item.y + $item.height / 2)
Op 'input.pointer' @{ action = 'press'; x = $h.x; y = $h.y } | Out-Null
Start-Sleep -Milliseconds 200
for ($i = 1; $i -le 6; $i++) {
    Op 'input.pointer' @{ action = 'move'
        x = [int]($h.x + ($target.x - $h.x) * $i / 6)
        y = [int]($h.y + ($target.y - $h.y) * $i / 6); leftDown = $true } | Out-Null
    Start-Sleep -Milliseconds 60
}
Op 'input.pointer' @{ action = 'release'; x = $target.x; y = $target.y } | Out-Null
Start-Sleep -Milliseconds 600

$item2 = (Op 'object.find' @{ type = 'text' }).result.items[0]
Write-Host ("after handle drag: frameWidth={0}  bounds={1}x{2}" -f $item2.frameWidth, [math]::Round($item2.width,2), [math]::Round($item2.height,2))
Shots 'editbox-2'
Write-Host "done"
