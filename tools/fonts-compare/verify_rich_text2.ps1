# Verifies rich-text styling: restyling a selected range must change only that range and
# must not reflow the whole block.
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
function Click([double]$mx, [double]$my, [bool]$shift) {
    $p = Aim $mx $my
    Op 'input.pointer' @{ action = 'press';   x = $p.x; y = $p.y; shift = $shift } | Out-Null
    Start-Sleep -Milliseconds 120
    Op 'input.pointer' @{ action = 'release'; x = $p.x; y = $p.y } | Out-Null
    Start-Sleep -Milliseconds 250
}
function Runs() { (Op 'text.runs' @{}).result }

Op 'document.new' @{ name = 'Rich text' } | Out-Null
Start-Sleep -Milliseconds 800
# Close every document except the one just created. Closing only 'Untitled' ones left
# stale copies behind, and a later read then measured a different document.
$list = (Op 'document.list' @{}).result
$keep = ($list | Where-Object { $_.active })[0].index
for ($i = $list.Count - 1; $i -ge 0; $i--) {
    if ($list[$i].index -ne $keep) {
        Op 'document.close' @{ index = $list[$i].index } | Out-Null
        Start-Sleep -Milliseconds 400
    }
}
$board = (Op 'artboard.list' @{}).result[0]
Op 'artboard.setBounds' @{ artboardId = $board.artboardId; x = 0; y = 0
                           width = 595.276; height = 841.89 } | Out-Null
Op 'view.fit' @{} | Out-Null
Start-Sleep -Milliseconds 800
$mine = ((Op 'document.list' @{}).result | Where-Object { $_.name -eq 'Rich text' })[0]
Op 'document.select' @{ index = $mine.index } | Out-Null
Start-Sleep -Milliseconds 500

Op 'tool.set' @{ tool = 'text' } | Out-Null
$start = Aim 60 200
Op 'input.pointer' @{ action = 'press';   x = $start.x; y = $start.y } | Out-Null
Start-Sleep -Milliseconds 150
Op 'input.pointer' @{ action = 'release'; x = $start.x; y = $start.y } | Out-Null
Start-Sleep -Milliseconds 300
Op 'input.type' @{ text = "Hello world and goodbye" } | Out-Null
Start-Sleep -Milliseconds 900
Write-Host "typed: '$((Runs).plain)'  runs=$((Runs).runs.Count)"

# Caret to the start of "world" (index 6), then shift-click after "world" (index 11).
Write-Host "select 'world' (6..11) with a shift-click"
Click 60 203 $false
Write-Host "  caret after first click: $((Op 'text.caret' @{}).result.selectionStart)"
Click 106 203 $true
$sel = (Op 'text.caret' @{}).result
Write-Host "  selection: $($sel.selectionStart)..$($sel.selectionEnd)"

if ($sel.selectionEnd -le $sel.selectionStart) {
    Write-Host "RESULT: could not make a range selection"
    exit 1
}

Write-Host "`nrestyle only that range to mono bold italic"
Op 'text.update' @{ family = 'Nimbus Mono PS'; fontSize = 12; bold = $true; italic = $true
                    color = @(0, 0, 0) } | Out-Null
Start-Sleep -Milliseconds 700

$r = Runs
Write-Host "  runs now: $($r.runs.Count)"
foreach ($run in $r.runs) {
    Write-Host ("    '{0}'  {1} {2}pt bold={3} italic={4}" -f $run.text, $run.family, $run.size, $run.bold, $run.italic)
}
if ($r.plain -ne 'Hello world and goodbye') {
    Write-Host "  RESULT: text was corrupted: '$($r.plain)'"
} elseif ($r.runs.Count -gt 1) {
    Write-Host "  RESULT: block split into $($r.runs.Count) runs - only the selection was restyled"
} else {
    Write-Host "  RESULT: still one run - the whole block was restyled"
}
