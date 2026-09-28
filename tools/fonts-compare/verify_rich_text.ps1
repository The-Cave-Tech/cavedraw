# Verifies rich-text styling: changing the face of a selected range must change only that
# range, not reflow the whole block.
$ErrorActionPreference = 'Stop'
function Op($op, $params) {
    $b = @{ op = $op; params = $params } | ConvertTo-Json -Depth 10
    (Invoke-RestMethod -Uri 'http://127.0.0.1:5099/api/v1/invoke' -Method Post `
        -ContentType 'application/json' -Body $b -TimeoutSec 300)
}
function Aim([double]$modelX, [double]$modelY) {
    $p = (Op 'view.toScreen' @{ x = $modelX; y = $modelY }).result
    @{ x = [int]$p.x; y = [int]$p.y }
}
function Runs() { (Op 'text.runs' @{}).result }

Op 'document.new' @{ name = 'Rich text' } | Out-Null
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

$mine = ((Op 'document.list' @{}).result | Where-Object { $_.name -eq 'Rich text' })[0]
Op 'document.select' @{ index = $mine.index } | Out-Null
Start-Sleep -Milliseconds 500

Write-Host "type a line"
Op 'tool.set' @{ tool = 'text' } | Out-Null
$p = Aim 80 140
Op 'input.pointer' @{ action = 'press';   x = $p.x; y = $p.y } | Out-Null
Start-Sleep -Milliseconds 150
Op 'input.pointer' @{ action = 'release'; x = $p.x; y = $p.y } | Out-Null
Start-Sleep -Milliseconds 300
Op 'input.type' @{ text = "Hello world and goodbye" } | Out-Null
Start-Sleep -Milliseconds 900
Write-Host "  runs: $((Runs).runs.Count)  plain: '$((Runs).plain)'"

Write-Host "`nselect characters 6..11 ('world') by shift-clicking, then make it bold italic"
# Put the caret at 6 first: click near the start, then extend with shift-moves.
Op 'input.pointer' @{ action = 'press'; x = ($p.x + 3); y = $p.y } | Out-Null
Start-Sleep -Milliseconds 150
Op 'input.pointer' @{ action = 'release'; x = ($p.x + 3); y = $p.y } | Out-Null
Start-Sleep -Milliseconds 250
Write-Host "  caret after click: $((Op 'text.caret' @{}).result.selectionStart)"

# Extend the selection by dragging to the right with the button held.
$from = Aim 80 140
$to = Aim 130 140
Op 'input.pointer' @{ action = 'press'; x = $from.x; y = $from.y } | Out-Null
Start-Sleep -Milliseconds 150
for ($i = 1; $i -le 8; $i++) {
    Op 'input.pointer' @{ action = 'move'
        x = [int]($from.x + ($to.x - $from.x) * $i / 8)
        y = $from.y; leftDown = $true } | Out-Null
    Start-Sleep -Milliseconds 50
}
Op 'input.pointer' @{ action = 'release'; x = $to.x; y = $to.y } | Out-Null
Start-Sleep -Milliseconds 500
$sel = Op 'text.caret' @{}
Write-Host "  selection: $($sel.result.selectionStart)..$($sel.result.selectionEnd)"

if ($sel.result.selectionEnd -gt $sel.result.selectionStart) {
    Op 'text.update' @{ family = 'Nimbus Mono PS'; fontSize = 12; bold = $true; italic = $true
                        color = @(0, 0, 0) } | Out-Null
    Start-Sleep -Milliseconds 600
    $r = Runs
    Write-Host "`n  after styling the range:"
    foreach ($run in $r.runs) {
        Write-Host ("    '{0}'  {1} {2}pt bold={3} italic={4}" -f $run.text, $run.family, $run.size, $run.bold, $run.italic)
    }
    if ($r.runs.Count -gt 1) {
        Write-Host "  RESULT: the block split into $($r.runs.Count) runs - only the selection was restyled"
    } else {
        Write-Host "  RESULT: still one run - the whole block was restyled"
    }
} else {
    Write-Host "  RESULT: could not make a selection"
}
