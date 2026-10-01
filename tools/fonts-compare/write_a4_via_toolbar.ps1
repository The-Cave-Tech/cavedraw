# Writes an A4 lorem ipsum document by driving the SAME controls a person uses:
# the text tool and pointer to draw each frame, the keyboard to type at 50 wpm, and the
# text toolbar's own controls (TtFont, TtSize, TtBold, ...) to set the type.
#
# Nothing here calls text.update or text.style. If this works, a person can do it.
$ErrorActionPreference = 'Stop'
$WPM = 50
$MS_PER_CHAR = 240

function Op($op, $params) {
    $b = @{ op = $op; params = $params } | ConvertTo-Json -Depth 10
    (Invoke-RestMethod -Uri 'http://127.0.0.1:5099/api/v1/invoke' -Method Post `
        -ContentType 'application/json' -Body $b -TimeoutSec 600)
}

function Find($name) {
    $r = (Op 'ui.find' @{ name = $name; includeHidden = $true }).result.controls
    if (-not $r -or $r.Count -eq 0) { throw "control $name not found" }
    return $r[0]
}

function SetCtl($name, $value) {
    Op 'ui.setValue' @{ name = $name; value = "$value" } | Out-Null
    Start-Sleep -Milliseconds 220
}

function ClickCtl($name) {
    Op 'ui.click' @{ name = $name } | Out-Null
    Start-Sleep -Milliseconds 220
}

function DrawFrame([double]$x, [double]$y, [double]$w) {
    Op 'tool.set' @{ tool = 'text' } | Out-Null
    $a = Px $x $y
    $b = Px ($x + $w) $y
    Op 'input.pointer' @{ action = 'press';   x = $a.x; y = $a.y } | Out-Null
    Start-Sleep -Milliseconds 200
    Op 'input.pointer' @{ action = 'release'; x = $b.x; y = $b.y } | Out-Null
    Start-Sleep -Milliseconds 300
}

# Leaving edit mode first is what starts a NEW block on the next press; without it the
# click lands inside the block still being edited and moves its caret instead.
function EndBlock() {
    Op 'ui.keys' @{ keys = 'Escape' } | Out-Null
    Start-Sleep -Milliseconds 300
}

function TypePaced([string]$text) {
    Op 'input.type' @{ text = $text; wpm = $WPM } | Out-Null
    $seconds = [Math]::Ceiling($text.Length * $MS_PER_CHAR / 1000.0) + 1
    Write-Host ("    typing {0} characters (~{1}s)..." -f $text.Length, $seconds)
    Start-Sleep -Seconds $seconds
}

Op 'document.new' @{ name = 'A4 Lorem Ipsum' } | Out-Null
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

Write-Host "1. masthead - Nimbus Roman 28 bold"
DrawFrame 55 55 490
TypePaced "De Finibus Bonorum et Malorum"
SetCtl 'TtFont' 'Nimbus Roman'
SetCtl 'TtSize' '28'
ClickCtl 'TtBold'
SetCtl 'TtColor' '18,26,56'
EndBlock

Write-Host "2. standfirst - italic sans 11"
DrawFrame 55 96 490
TypePaced "Section I - the standard placeholder text of the printing trade"
SetCtl 'TtFont' 'Nimbus Sans'
SetCtl 'TtSize' '11'
ClickCtl 'TtBold'
ClickCtl 'TtItalic'
SetCtl 'TtColor' '110,110,110'
EndBlock

Write-Host "3. body, left column"
DrawFrame 55 150 228
TypePaced ("Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod " +
           "tempor incididunt ut labore et dolore magna aliqua.")
SetCtl 'TtFont' 'Nimbus Roman'
SetCtl 'TtSize' '10'
ClickCtl 'TtItalic'
SetCtl 'TtColor' '24,24,24'
SetCtl 'TtLineSpacing' '1.45'
SetCtl 'TtParagraphSpacing' '8'
EndBlock

Write-Host "4. body, right column"
DrawFrame 312 150 228
TypePaced ("Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi " +
           "ut aliquip ex ea commodo consequat.")
SetCtl 'TtFont' 'Nimbus Sans'
SetCtl 'TtSize' '9.5'
SetCtl 'TtColor' '24,24,24'
SetCtl 'TtLineSpacing' '1.35'
SetCtl 'TtParagraphSpacing' '10'
EndBlock

Write-Host "5. pull quote, rotated, centred"
DrawFrame 55 340 430
TypePaced "Neque porro quisquam est qui dolorem ipsum quia dolor sit amet"
SetCtl 'TtFont' 'Nimbus Roman'
SetCtl 'TtSize' '17'
ClickCtl 'TtBold'
ClickCtl 'TtItalic'
SetCtl 'TtColor' '150,40,40'
SetCtl 'TtRotation' '-6'
SetCtl 'TtAlign' 'Center'
EndBlock

Write-Host "6. section heading and copy"
DrawFrame 55 470 490
TypePaced ("At vero eos et accusamus et iusto odio dignissimos ducimus qui blanditiis " +
           "praesentium voluptatum deleniti atque corrupti.")
SetCtl 'TtFont' 'Nimbus Roman'
SetCtl 'TtSize' '10'
ClickCtl 'TtBold'
SetCtl 'TtColor' '30,60,40'
SetCtl 'TtLineSpacing' '1.4'
SetCtl 'TtRotation' '0'
EndBlock

Write-Host "7. footer, mono"
DrawFrame 55 780 490
TypePaced "Typeset in Nimbus Roman, Nimbus Sans and Nimbus Mono"
SetCtl 'TtFont' 'Nimbus Mono PS'
SetCtl 'TtSize' '8'
ClickCtl 'TtBold'
SetCtl 'TtColor' '140,140,140'
EndBlock

Op 'tool.set' @{ tool = 'select' } | Out-Null
Op 'selection.clear' @{} | Out-Null
Op 'view.fit' @{} | Out-Null
Start-Sleep -Milliseconds 500
Write-Host "text objects: $((Op 'object.find' @{ type = 'text' }).result.count)"
