# Writes an A4 lorem ipsum document by driving the editor with injected input.
# Text is created and edited through real pointer and keyboard events, not by writing
# objects into the model.
$ErrorActionPreference = 'Stop'

function Op($op, $params) {
    $b = @{ op = $op; params = $params } | ConvertTo-Json -Depth 10
    (Invoke-RestMethod -Uri 'http://127.0.0.1:5099/api/v1/invoke' -Method Post `
        -ContentType 'application/json' -Body $b -TimeoutSec 300)
}

# ---- A4 page ---------------------------------------------------------------
Op 'document.new' @{ name = 'A4 Lorem Ipsum' } | Out-Null
Start-Sleep -Milliseconds 400
$board = (Op 'artboard.list' @{}).result[0]
Op 'artboard.setBounds' @{
    artboardId = $board.artboardId
    x = 0; y = 0
    width = 595.276; height = 841.89
} | Out-Null
Start-Sleep -Milliseconds 300
Op 'view.fit' @{} | Out-Null
Start-Sleep -Milliseconds 300

# Map a point on the A4 page (points, top-left origin) to window pixels.
$zoom = (Op 'view.status' @{}).result.zoom
$canvasCentre = @{ x = 520; y = 520 }   # centre of the drawing area in window pixels
$viewCentre = (Op 'view.center' @{}).result

function ScreenOf([double]$px, [double]$py) {
    # page point -> document point (artboard may be offset), then -> window pixel
    $dx = $px
    $dy = $py
    return @{
        x = [int]($canvasCentre.x + ($dx - $viewCentre.x) * $zoom)
        y = [int]($canvasCentre.y + ($dy - $viewCentre.y) * $zoom)
    }
}

function WriteText([double]$px, [double]$py, [string]$text) {
    Op 'tool.set' @{ tool = 'text' } | Out-Null
    $s = ScreenOf $px $py
    Op 'input.pointer' @{ x = $s.x; y = $s.y } | Out-Null
    Start-Sleep -Milliseconds 250
    Op 'input.type' @{ text = $text } | Out-Null
    Start-Sleep -Milliseconds 250
    Op 'tool.set' @{ tool = 'select' } | Out-Null
    Start-Sleep -Milliseconds 150
}

function StyleSelected([hashtable]$style) {
    Op 'text.update' $style | Out-Null
    Start-Sleep -Milliseconds 200
}

$lorem1 = "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod " +
          "tempor incididunt ut labore et dolore magna aliqua."

Write-Host "title..."
WriteText 60 70 "De Finibus Bonorum et Malorum"
StyleSelected @{ family = 'Nimbus Roman'; fontSize = 26; bold = $true; color = @(20, 30, 60) }

Write-Host "subtitle..."
WriteText 60 110 "Section I - the standard placeholder text"
StyleSelected @{ family = 'Nimbus Sans'; fontSize = 12; bold = $false; italic = $true;
                 color = @(120, 120, 120) }

Write-Host "body 1..."
WriteText 60 150 $lorem1
StyleSelected @{ family = 'Nimbus Roman'; fontSize = 11; color = @(20, 20, 20) }

Write-Host "body 2..."
WriteText 60 200 ("Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris " +
                  "nisi ut aliquip ex ea commodo consequat.")
StyleSelected @{ family = 'Nimbus Roman'; fontSize = 11; color = @(20, 20, 20) }

Write-Host "pull quote (rotation)..."
WriteText 60 280 "Duis aute irure dolor in reprehenderit"
StyleSelected @{ family = 'Nimbus Sans'; fontSize = 16; bold = $true; italic = $true;
                 color = @(150, 40, 40) }
Start-Sleep -Milliseconds 200
Op 'object.transform' @{ rotationDegrees = -8 } | Out-Null
Start-Sleep -Milliseconds 200

Write-Host "columns..."
WriteText 60 380 "Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia."
StyleSelected @{ family = 'Nimbus Sans'; fontSize = 10; color = @(30, 90, 60) }
WriteText 320 380 "Deserunt mollit anim id est laborum, sed ut perspiciatis unde omnis."
StyleSelected @{ family = 'Nimbus Sans'; fontSize = 10; color = @(30, 90, 60) }

Write-Host "mono caption..."
WriteText 60 440 "REF 3464 / LILLIE - A4 - 210 x 297 mm"
StyleSelected @{ family = 'Nimbus Mono PS'; fontSize = 9; color = @(90, 90, 90) }

Op 'selection.clear' @{} | Out-Null
Op 'view.fit' @{} | Out-Null
Write-Host "done: $((Op 'object.find' @{ type='text' }).result.count) text objects"
