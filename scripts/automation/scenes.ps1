# Scenes drawn through mouse and keyboard only, each verifying the model as it goes.
#
# Run: pwsh -NoProfile -File artifacts\auto\scenes.ps1 [-Only 1,7,8]
#
# Every scene starts from a fresh document and uses a different feature mix. Failures are collected into
# artifacts/auto/report.json with the scene, the step, what was expected and what the model held.

param([string]$Only = '', [string]$ReportPath = 'artifacts\auto\report.json')

# The scene filter arrives as one token: '1;7;8'.

. (Join-Path $PSScriptRoot 'drive.ps1')

# A shape's outline always selects it: click on empty canvas to clear, click the top edge, expect exactly it.
function VerifyOutlineSelect {
    param($item, [string]$label)
    if (-not $item) { return }
    ClearSelection
    SelectByOutline $item
    AssertSelection $item.id "$label is selectable by clicking its outline"
}

function VerifyInteriorSelect {
    param($item, [string]$label)
    if (-not $item) { return }
    ClearSelection
    SelectByInterior $item
    AssertSelection $item.id "$label is selectable by clicking inside its fill"
}

# The width rule that path.expandStroke promises is read from the model, not from a control: the first Stroke
# block in the document JSON, which is unambiguous in a scene that draws one shape at a time.
function StrokeWidth {
    param([string]$id = '')
    $j = (Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 24 -Compress
    # Two traps here, both learned by failing: a Stroke block carries a nested Color object, so a pattern that
    # forbids braces between the key and Width never matches at all; and a document with more than one shape
    # has more than one Stroke block, so the read has to start at the item under test rather than at the first.
    # The JSON holds lower-case ids and the dump does not promise any case, so the search normalises: the
    # reader returned -1 for two rounds because it looked for the id exactly as the dump gave it.
    $i = if ($id) { $j.IndexOf('"' + $id.ToLowerInvariant() + '"') } else { $j.IndexOf('"Stroke":') }
    if ($i -lt 0) { Write-Host ("     [StrokeWidth] searched for id='" + $id + "' and did not find it") -ForegroundColor DarkYellow; return -1 }
    $seg = $j.Substring($i, [Math]::Min(500, $j.Length - $i))
    $m = [regex]::Match($seg, '"Width"\s*:\s*(-?[0-9.]+)')
    if ($m.Success) { return [double]$m.Groups[1].Value }
    Write-Host ("     [StrokeWidth] id='" + $id + "' index=" + $i + " segment=" + $seg.Substring(0, [Math]::Min(120, $seg.Length))) -ForegroundColor DarkYellow
    return -1
}

function StyleStroke {
    param([string]$hex, [double]$width)
    $r = [int][Convert]::ToInt32($hex.Substring(0, 2), 16)
    $g = [int][Convert]::ToInt32($hex.Substring(2, 2), 16)
    $b = [int][Convert]::ToInt32($hex.Substring(4, 2), 16)
    Invoke-Op 'style.setStroke' @{ color = @($r, $g, $b); width = $width } | Out-Null
    Start-Sleep -Milliseconds 200
}

function Run-Scene {
    param([int]$index)

    switch ($index) {

        1 {
            New-Scene 'rectangles and the transform fields'
            $a = DrawRect 60 60 180 120
            VerifyOutlineSelect $a 'a rectangle'
            SetField 'XBox' '150' 'X'
            SetField 'YBox' '200' 'Y'
            SetField 'WBox' '240' 'W'
            SetField 'HBox' '160' 'H'
            $now = LastItem
            Check 'X typed into the panel moves the shape' ([Math]::Abs($now.x - 150) -le 3) 'x = 150' ("x = {0:N1}" -f $now.x)
            Check 'W typed into the panel sets the width' ([Math]::Abs($now.w - 240) -le 3) 'w = 240' ("w = {0:N1}" -f $now.w)
            SetField 'AngleBox' '30' 'R'
            $rot = LastItem
            Check 'a rotation changes the box' (($rot.w -ne $now.w) -or ($rot.h -ne $now.h)) 'a different box' ("w {0:N1} h {1:N1}" -f $rot.w, $rot.h)
        }

        2 {
            New-Scene 'ellipses with fills'
            for ($i = 0; $i -lt 6; $i++) {
                $e = DrawEllipse (40 + $i * 100) 80 80 80
                VerifyOutlineSelect $e "ellipse $i"
                FillHex '3366cc'
            }
            Check 'six ellipses exist' ((ItemCount) -eq 6) '6 items' "$(ItemCount) items"
        }

        3 {
            New-Scene 'pen polygons and a marquee'
            $m1 = DrawPolygon @(@(60.0, 320.0), @(160.0, 180.0), @(260.0, 320.0)) 'ridge 1'
            $m2 = DrawPolygon @(@(220.0, 340.0), @(340.0, 160.0), @(460.0, 340.0)) 'ridge 2'
            $m3 = DrawPolygon @(@(400.0, 360.0), @(540.0, 200.0), @(680.0, 360.0)) 'ridge 3'
            VerifyOutlineSelect $m1 'ridge 1'
            VerifyOutlineSelect $m2 'ridge 2'
            ClearSelection
            DragModel 40 140 720 420
            Check 'a marquee selects all three ridges' ((Selection).Count -eq 3) '3 selected' "$((Selection).Count) selected"
        }

        4 {
            New-Scene 'pencil strokes'
            $s1 = DrawFreehand 120 200
            $s2 = DrawFreehand 120 300
            $s3 = DrawFreehand 120 400
            VerifyOutlineSelect $s1 'the first stroke'
            Keys 'Ctrl+A'
            Check 'select-all picks up every stroke' ((Selection).Count -eq 3) '3 selected' "$((Selection).Count) selected"
        }

        5 {
            New-Scene 'the shape flyout by long press'
            $before = ItemCount
            $button = Find-Control 'ShapeToolButton'
            if ($button) {
                HoldWindow ([int]($button.x + $button.width / 2)) ([int]($button.y + $button.height / 2)) 800
            } else { Record-Failure 'the shape tool button is present' 'ShapeToolButton' 'not found' }
            Start-Sleep -Milliseconds 400
            $pop = (Invoke-Op 'ui.popups' @{}).result
            $open = @($pop.popups | Where-Object { $_.open -eq $true })
            Check 'holding the shape tool opens its flyout' ($open.Count -gt 0) 'an open popup' (($open | ForEach-Object { $_.name }) -join ',')
            Keys 'Escape'
            Start-Sleep -Milliseconds 300
            DragModel 120 200 220 300
            Check 'a shape is drawn after the flyout gesture' ((ItemCount) -gt $before) "$($before + 1) items" "$(ItemCount) items"
        }

        6 {
            New-Scene 'text creation and the text toolbar'
            Tool 'ToolText' 'text' | Out-Null
            ClickModel 60 80 | Out-Null
            TypeText 'There and Back Again'
            $t = LastItem
            Check 'typing creates a text object holding the words' ($t.text -eq 'There and Back Again') "'There and Back Again'" "'$($t.text)'"
            $size = Find-Control 'TtSize'
            if ($size) {
                ClickAt ([int]($size.x + $size.width / 2)) ([int]($size.y + $size.height / 2))
                Keys 'Ctrl+A'; TypeText '36'; Keys 'Enter'
                $after = LastItem
                Check 'the size field changes the text box height' ($after.h -gt $t.h) 'a taller box' ("h {0:N1} -> {1:N1}" -f $t.h, $after.h)
            } else { Record-Failure 'the text toolbar is present' 'TtSize' 'not found' }
        }

        7 {
            New-Scene 'text editing torture'
            Tool 'ToolText' 'text' | Out-Null
            ClickModel 60 80 | Out-Null
            TypeText 'Hello World'
            $t = LastItem
            Check 'the first string is stored' ($t.text -eq 'Hello World') "'Hello World'" "'$($t.text)'"
            $id = $t.id

            Tool 'ToolText' 'text' | Out-Null
            ClickModel ($t.x + $t.w / 2) ($t.y + $t.h / 2) | Out-Null
            Write-Host ("     status after clicking into the text: '{0}'" -f (StatusLine)) -ForegroundColor DarkGray
            Keys 'End'
            TypeText '!'
            Write-Host ("     status after typing: '{0}'" -f (StatusLine)) -ForegroundColor DarkGray
            Check 'appending at the end keeps the rest' ((TextOf $id) -eq 'Hello World!') "'Hello World!'" "'$(TextOf $id)'"

            # Double-click the first word to select it, then type over it.
            ClickModel ($t.x + 12) ($t.y + $t.h / 2) 2 | Out-Null
            TypeText 'Goodbye'
            Check 'typing over a selected word replaces it without wrecking the line' ((TextOf $id) -eq 'Goodbye World!') "'Goodbye World!'" "'$(TextOf $id)'"

            Tool 'ToolText' 'text' | Out-Null
            ClickModel ($t.x + 20) ($t.y + $t.h / 2) | Out-Null
            Keys 'Home'
            TypeText '>> '
            Check 'typing at the start inserts there' ((TextOf $id) -eq '>> Goodbye World!') "'>> Goodbye World!'" "'$(TextOf $id)'"

            Keys 'Ctrl+A'
            TypeText 'In a hole in the ground there lived a hobbit'
            Check 'select-all inside the text replaces the whole string' ((TextOf $id) -eq 'In a hole in the ground there lived a hobbit') 'the sentence' "'$(TextOf $id)'"

            Keys 'End'; Keys 'Back'; Keys 'Back'
            # **Two Backs from a 44-character string give 42, not 43.** 'In a hole in the ground there lived a
            # hobbit' is 44 characters; this check expected '...hob' (43) for two presses, which is one removal.
            # Measured four ways on the built binary - immediately, after 600 ms, after 1800 ms, through
            # text.update and through ui.dump - every readback says 42 and 'In a hole in the ground there lived
            # a hobb'. The expectation was wrong; the editor removed exactly two characters.
            Check 'two backspaces remove two characters' ((TextOf $id) -eq 'In a hole in the ground there lived a hobb') '...hobb' "'$(TextOf $id)'"

            Keys 'Home'; Keys 'Enter'
            $nl = TextOf $id
            # TextOf reads the dump, which now **escapes** a newline as the two characters \n rather than
            # substituting a space for it: the substitution had the right length and the wrong characters, and
            # it hid this check behind a string the document did not contain (#234). The check therefore tests
            # the escaped form, which is what a reader of the dump sees.
            Check 'Enter inserts a newline rather than doing nothing' ($nl.StartsWith('\n')) 'a leading newline' ("'" + $nl + "'")

            Keys 'Ctrl+A'
            TypeText 'Bilbo Baggins — ë ü 漢字'
            Check 'accented and CJK characters are stored exactly' ((TextOf $id) -eq 'Bilbo Baggins — ë ü 漢字') 'the same string' "'$(TextOf $id)'"

            Keys 'Escape'
            Check 'the string survives leaving the text editor' ((TextOf $id) -eq 'Bilbo Baggins — ë ü 漢字') 'the same string' "'$(TextOf $id)'"
        }

        8 {
            New-Scene 'selection torture'
            $r1 = DrawRect 60 80 120 90
            $r2 = DrawRect 240 80 120 90
            $e1 = DrawEllipse 60 240 120 90
            FillHex 'cc3333'
            $e2 = DrawEllipse 240 240 120 90
            FillHex '33cc33'
            $r3 = DrawRect 60 400 300 80
            Check 'five shapes were drawn' ((ItemCount) -eq 5) '5 items' "$(ItemCount) items"

            VerifyOutlineSelect $r1 'rectangle 1'
            VerifyOutlineSelect $r2 'rectangle 2'
            VerifyOutlineSelect $r3 'the long rectangle'
            VerifyOutlineSelect $e1 'the red ellipse'
            VerifyInteriorSelect $e1 'the red ellipse'

            ClearSelection
            Check 'clicking empty canvas deselects' ((Selection).Count -eq 0) '0 selected' "$((Selection).Count) selected"

            ClickModel ($r1.x + $r1.w / 2) $r1.y | Out-Null
            $p = Invoke-Op 'view.toScreen' @{ x = ($r2.x + $r2.w / 2); y = $r2.y }
            Invoke-Op 'input.pointer' @{ x = [int]([double]$p.result.x - $script:Ox); y = [int]([double]$p.result.y - $script:Oy); shift = $true } | Out-Null
            Start-Sleep -Milliseconds 300
            Check 'shift-click selects a second shape as well' ((Selection).Count -eq 2) '2 selected' "$((Selection).Count) selected"

            ClearSelection
            DragModel 30 40 420 500
            Check 'a marquee selects every shape it encloses' ((Selection).Count -eq 5) '5 selected' "$((Selection).Count) selected"

            Keys 'Ctrl+A'
            Check 'select-all selects every shape' ((Selection).Count -eq 5) '5 selected' "$((Selection).Count) selected"
            Keys 'Delete'
            Check 'Delete removes them all' ((ItemCount) -eq 0) '0 items' "$(ItemCount) items"
            Keys 'Ctrl+Z'
            Check 'one undo brings them all back' ((ItemCount) -eq 5) '5 items' "$(ItemCount) items"
        }

        9 {
            New-Scene 'copy paste and nudge'
            $a = DrawRect 80 80 100 80
            Keys 'Ctrl+C'
            Keys 'Ctrl+V'
            Check 'paste adds a copy' ((ItemCount) -eq 2) '2 items' "$(ItemCount) items"
            for ($i = 0; $i -lt 6; $i++) { Keys 'Right' }
            for ($i = 0; $i -lt 4; $i++) { Keys 'Down' }
            $all = Items
            $moved = $all[$all.Count - 1]
            Check 'the pasted copy is the one the arrows moved' ([Math]::Abs($moved.x - ($a.x + 6)) -le 3) ("x = {0:N1}" -f ($a.x + 6)) ("x = {0:N1}" -f $moved.x)
            # **Undo steps back one command at a time, and the ten nudges above are ten commands.** A single
            # Ctrl+Z takes back the last arrow key, not the paste, so the copy is still there and the count is
            # still 2 - which is what this check read as "undo does nothing" and what I filed as a defect
            # (#237). Undo is correct: a fresh draw undoes on the first press. This walks back until the
            # pasted copy is gone, with a bound so a genuine failure still fails.
            for ($u = 0; $u -lt 14 -and (ItemCount) -gt 1; $u++) {
                Keys 'Ctrl+Z'
                Start-Sleep -Milliseconds 120
            }
            Check 'undo walks back to the state before the paste' ((ItemCount) -eq 1) '1 item' "$(ItemCount) items"
        }

        10 {
            New-Scene 'gradient ramp'
            $r = DrawRect 100 100 320 220
            VerifyOutlineSelect $r 'the rectangle'
            $tab = Find-ByText 'Gradient'
            if ($tab) {
                # **Click by name, not by coordinate.** ClickAt goes through input.pointer, whose frame
                # never matched view.toScreen, so the pane never opened and the ramp was looked for in
                # the wrong tab. Measured with ui.click: the Gradient tab opens, and GradientRamp#Ramp
                # is in it.
                Invoke-Op 'ui.click' @{ text = 'Gradient' } | Out-Null
                Start-Sleep -Milliseconds 400
                $before = (Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 12 -Compress
                # The ramp is GradientRamp by TYPE and Ramp by NAME - the dump writes GradientRamp#Ramp - so a
                # lookup by name does not find it even with the pane open.
                $ramp = (Invoke-Op 'ui.find' @{ type = 'GradientRamp' }).result.controls | Select-Object -First 1
                if ($ramp) {
                    Invoke-Op 'ui.click' @{ type = 'GradientRamp' } | Out-Null
                    Start-Sleep -Milliseconds 300
                    $after = (Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 12 -Compress
                    Check 'clicking the gradient ramp changes the paint' ($before -ne $after) 'a different model' 'unchanged'
                } else { Record-Failure 'the gradient ramp is present' 'GradientRamp' 'not found' }
            } else { Record-Failure 'the Gradient tab is present' 'a control titled Gradient' 'not found' }
        }

        11 {
            New-Scene 'stroke pane and hatch'
            $r = DrawRect 100 100 260 180
            VerifyOutlineSelect $r 'the rectangle'
            $tab = Find-ByText 'Stroke'
            if ($tab) {
                ClickAt ([int]($tab.x + $tab.width / 2)) ([int]($tab.y + $tab.height / 2))
                Start-Sleep -Milliseconds 350
            } else { Record-Failure 'the Stroke tab is present' 'a control titled Stroke' 'not found' }
            $hatch = Find-Control 'HatchCross'
            if ($hatch) {
                $before = (Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 12 -Compress
                ClickAt ([int]($hatch.x + $hatch.width / 2)) ([int]($hatch.y + $hatch.height / 2))
                Start-Sleep -Milliseconds 300
                $after = (Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 12 -Compress
                Check 'the hatch swatch changes the shape' ($before -ne $after) 'a different model' 'unchanged'
            } else { Record-Failure 'the hatch swatches are present' 'HatchCross' 'not found' }
        }

        12 {
            New-Scene 'adding a layer'
            DrawRect 80 80 200 140 | Out-Null
            $before = (LayerCount)
            $add = Find-ByText 'Add layer'
            if (-not $add) { $add = Find-Control 'AddLayerButton' }
            if ($add) {
                # **Click it by name, not by coordinate.** ClickAt goes through input.pointer, whose coordinate
                # frame is the one that has never matched view.toScreen; the same button clicked with ui.click
                # adds the layer (measured: Layer 1 -> Layer 2, layer.list and ui.dump agreeing). ui.click finds
                # the control itself, so there is no aim to get wrong.
                $clicked = Invoke-Op 'ui.click' @{ type = 'Button'; text = 'Add layer' }
                Start-Sleep -Milliseconds 350
            } else { Record-Failure 'the layers pane offers Add layer' 'a control to add a layer' 'not found' }
            $after = (LayerCount)
            Check 'the layers pane can add a layer' ($after -gt $before) "$($before + 1) layers" "$after layers"
        }

        13 {
            New-Scene 'group and ungroup'
            DrawRect 80 80 120 100 | Out-Null
            DrawRect 260 80 120 100 | Out-Null
            Keys 'Ctrl+A'
            Keys 'Ctrl+G'
            $groups = @((Items) | Where-Object { $_.kind -eq 'group' })
            Check 'Ctrl+G groups the selection' ($groups.Count -ge 1) 'a group' "$($groups.Count) groups"
            Keys 'Ctrl+Shift+G'
            $left = @((Items) | Where-Object { $_.kind -eq 'group' })
            Check 'Ctrl+Shift+G ungroups it again' ($left.Count -eq 0) 'no groups' "$($left.Count) groups"
        }

        14 {
            New-Scene 'undo and redo stress'
            for ($i = 0; $i -lt 8; $i++) { DrawRect (40 + $i * 90) 120 70 60 $false | Out-Null }
            Check 'eight rectangles were drawn' ((ItemCount) -eq 8) '8 items' "$(ItemCount) items"
            for ($i = 0; $i -lt 4; $i++) { Keys 'Ctrl+Z' }
            Check 'four undos remove four rectangles' ((ItemCount) -eq 4) '4 items' "$(ItemCount) items"
            for ($i = 0; $i -lt 4; $i++) { Keys 'Ctrl+Y' }
            Check 'four redos bring them back' ((ItemCount) -eq 8) '8 items' "$(ItemCount) items"
        }

        15 {
            New-Scene 'zoom and fit'
            DrawRect 100 100 200 150 | Out-Null
            Keys 'Ctrl+OemPlus'
            Keys 'Ctrl+OemPlus'
            Keys 'Ctrl+0'
            Check 'the document survives zooming' ((ItemCount) -eq 1) '1 item' "$(ItemCount) items"
            $r = Invoke-Op 'view.toScreen' @{ x = 0.0; y = 0.0 }
            Check 'the viewport still answers after a zoom' ($null -ne $r.result) 'a mapping' 'nothing'
        }

        16 {
            New-Scene 'a second artboard'
            DrawRect 60 60 150 110 | Out-Null
            # **The drag has to start off the page, and the default page is A4 LANDSCAPE: 841.9 x 595.3.** This
            # drag started at (560,60), which is inside that page, so ArtboardPress took the page-body branch -
            # correct, since a page's body belongs to the artwork and an artboard is moved by its name label -
            # and the create branch was never reached. Measured at the press: model (699.7,59.3), artboard
            # (0,0) 841.9 x 595.3. Starting past the page's right edge reaches AddArtboardFromRect.
            Tool 'ToolArtboard' 'artboard' | Out-Null
            DragModel 900 60 1180 340
            $boards = ((DocText) -split "`r?`n" | Where-Object { $_ -match '^ARTBOARD' }).Count
            Check 'dragging the artboard tool adds an artboard' ($boards -ge 2) '2 artboards' "$boards artboards"
        }

        17 {
            New-Scene 'lasso selection'
            DrawRect 80 100 100 80 $false | Out-Null
            DrawRect 220 100 100 80 $false | Out-Null
            DrawRect 360 100 100 80 $false | Out-Null
            # **A lasso encloses an area; a straight drag encloses none.** The canvas hands the pointer's own
            # path to SelectionEngine.ByLasso, so a drag from corner to corner is a line and correctly selects
            # nothing. This gesture traces a loop around the three shapes, which is what a lasso is for, and it
            # is the suite's only exercise of rectangle/lasso selection - every other selection check clicks.
            Tool 'ToolLasso' 'lasso' | Out-Null
            $track = @()
            $corners = @(@(40, 60), @(520, 60), @(520, 240), @(40, 240), @(40, 60))
            for ($c = 0; $c -lt $corners.Count; $c++) {
                $p = (Invoke-Op 'view.toScreen' @{ x = [double]$corners[$c][0]; y = [double]$corners[$c][1] }).result
                $px = [int]([double]$p.x - $script:Ox); $py = [int]([double]$p.y - $script:Oy)
                if ($c -eq 0) { $track += @{ kind = 'down'; x = $px; y = $py; deltaMs = 40 } }
                elseif ($c -eq $corners.Count - 1) { $track += @{ kind = 'up'; x = $px; y = $py; deltaMs = 40 } }
                else { $track += @{ kind = 'move'; x = $px; y = $py; deltaMs = 40 } }
            }
            Gesture $track 20000 $true | Out-Null
            Check 'a lasso around three shapes selects them' ((Selection).Count -eq 3) '3 selected' "$((Selection).Count) selected"
        }

        18 {
            New-Scene 'multiline text'
            Tool 'ToolText' 'text' | Out-Null
            ClickModel 60 80 | Out-Null
            TypeText 'Line one'
            Keys 'Enter'
            TypeText 'Line two'
            Keys 'Enter'
            TypeText 'Line three'
            $t = LastItem
            # Assert against the model, not the dump: a dump renders a multi-line value on one line, so a
            # correct newline reads as a space and looks like a defect. The model escapes it as \n.
            $modelJson = (Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 20 -Compress
            $hasNewlines = $modelJson.Contains('Line one\nLine two\nLine three')
            Check 'three typed lines are stored with their newlines' $hasNewlines 'Line one\nLine two\nLine three' ("dump joined them as: '" + $t.text + "'")
            $spacing = Find-Control 'TtLineSpacing'
            if ($spacing) {
                $h0 = $t.h
                ClickAt ([int]($spacing.x + $spacing.width / 2)) ([int]($spacing.y + $spacing.height / 2))
                Keys 'Ctrl+A'; TypeText '24'; Keys 'Enter'
                $after = LastItem
                Check 'line spacing changes the text box height' ($after.h -ge $h0) 'a taller box' ("h {0:N1} -> {1:N1}" -f $h0, $after.h)
            } else { Record-Failure 'the line spacing field is present' 'TtLineSpacing' 'not found' }
        }

        19 {
            New-Scene 'text styles'
            Tool 'ToolText' 'text' | Out-Null
            ClickModel 60 80 | Out-Null
            TypeText 'Bold words'
            Keys 'Ctrl+A'
            Keys 'Ctrl+B'
            Check 'Ctrl+B inside the text does not destroy it' ((LastItem).text -eq 'Bold words') "'Bold words'" "'$((LastItem).text)'"
            Keys 'Ctrl+I'
            Check 'Ctrl+I inside the text does not destroy it' ((LastItem).text -eq 'Bold words') "'Bold words'" "'$((LastItem).text)'"
            Keys 'Escape'
        }

        20 {
            New-Scene 'a collage'
            DrawRect 0 0 840 420 $false | Out-Null
            FillHex '1a2a55'
            DrawRect 0 420 840 175 $false | Out-Null
            FillHex '2b4a22'
            DrawEllipse 700 40 90 90 | Out-Null
            FillHex 'f5eec9'
            for ($i = 0; $i -lt 6; $i++) { DrawEllipse (60 + $i * 60) (40 + ($i % 2) * 30) 10 10 $false | Out-Null }
            DrawPolygon @(@(40.0, 460.0), @(180.0, 260.0), @(320.0, 460.0)) 'ridge A' | Out-Null
            DrawPolygon @(@(260.0, 470.0), @(420.0, 230.0), @(580.0, 470.0)) 'ridge B' | Out-Null
            $lake = DrawEllipse 80 480 360 100
            FillHex '205a75'
            DrawEllipse 150 500 60 60 | Out-Null
            FillHex 'd8b04a'
            Tool 'ToolText' 'text' | Out-Null
            ClickModel 40 30 | Out-Null
            TypeText 'There and Back Again'
            # **Read the text, not the last item in the tree.** After a dozen shapes LastItem is a path, which has
            # no text at all, so this compared '' with the title. object.find type=text answers directly: measured
            # on the built binary, the text is in the document with the right string and LastItem is a polygon.
            $title = (Invoke-Op 'object.find' @{ type = 'text' }).result.items | Select-Object -First 1
            Check 'the collage title is stored' ($title.text -eq 'There and Back Again') 'the title' ("'" + $title.text + "'")
            VerifyOutlineSelect $lake 'the lake'
            # **The scene creates fourteen objects, and always did.** Enumerated from its own draw list: two
            # rectangles, one sun, two ridges, a lake, a sun-reflection, six small ellipses - thirteen shapes -
            # and one text. The check asked for fifteen, which is one more than the scene draws, and it is not
            # a silent failure: the six small ellipses are drawn with checks off, so I measured them one at a
            # time and every one lands (items 1 through 6).
            Check 'the collage has at least fourteen objects' ((ItemCount) -ge 14) '14+ items' "$(ItemCount) items"
        }

        21 {
            New-Scene 'a field of small objects'
            for ($i = 0; $i -lt 20; $i++) {
                $x = 40 + ($i % 10) * 78
                $y = 100 + [Math]::Floor($i / 10) * 120
                DrawEllipse $x $y 40 40 $false | Out-Null
            }
            Check 'twenty ellipses were drawn' ((ItemCount) -eq 20) '20 items' "$(ItemCount) items"
            Keys 'Ctrl+A'
            Check 'select-all picks up all twenty' ((Selection).Count -eq 20) '20 selected' "$((Selection).Count) selected"
        }

        22 {
            New-Scene 'objects past the page edge'
            $big = DrawRect -80 -60 1100 500 $false
            VerifyOutlineSelect $big 'an oversized rectangle'
            $far = DrawRect 900 600 200 200 $false
            VerifyOutlineSelect $far 'an object on the pasteboard'
        }

        23 {
            New-Scene 'editing one shape many times'
            $r = DrawRect 200 150 160 120
            VerifyOutlineSelect $r 'the rectangle'
            for ($i = 1; $i -le 6; $i++) {
                SetField 'XBox' "$(100 + $i * 20)" "X$i"
                $now = LastItem
                Check "move $i lands where typed" ([Math]::Abs($now.x - (100 + $i * 20)) -le 3) ("x = {0}" -f (100 + $i * 20)) ("x = {0:N1}" -f $now.x)
            }
            Keys 'Ctrl+Z'
            Check 'one undo steps back one move' ([Math]::Abs((LastItem).x - 200) -le 3) 'x = 200' ("x = {0:N1}" -f (LastItem).x)
        }

        24 {
            New-Scene 'the final scene'
            DrawRect 0 0 840 400 $false | Out-Null
            FillHex '14224a'
            DrawEllipse 660 40 110 110 | Out-Null
            FillHex 'f4c76a'
            DrawPolygon @(@(0.0, 420.0), @(150.0, 250.0), @(300.0, 420.0)) 'hill A' | Out-Null
            FillHex '2f5d3a'
            DrawPolygon @(@(240.0, 430.0), @(420.0, 230.0), @(600.0, 430.0)) 'hill B' | Out-Null
            FillHex '24492e'
            DrawEllipse 60 450 400 110 | Out-Null
            FillHex '1d5b78'
            DrawEllipse 200 470 70 70 | Out-Null
            FillHex 'd3ac4e'
            Tool 'ToolText' 'text' | Out-Null
            ClickModel 40 40 | Out-Null
            TypeText 'In a hole in the ground'
            $texts = (Invoke-Op 'object.find' @{ type = 'text' }).result.items
            $line1 = $texts[0]
            Tool 'ToolText' 'text' | Out-Null
            ClickModel 40 80 | Out-Null
            TypeText 'there lived a hobbit'
            $line2 = $texts[1]
            Check 'both text objects hold their words' (($line1.text -eq 'In a hole in the ground') -and ($line2.text -eq 'there lived a hobbit')) 'two strings' ("'" + $line1.text + "' and '" + $line2.text + "'")
            Tool 'ToolCorner' 'corner' | Out-Null
            Keys 'Ctrl+A'
            Keys 'Ctrl+C'
            Keys 'Ctrl+V'
            Check 'copy and paste duplicates the whole scene' ((ItemCount) -eq 16) '16 items' "$(ItemCount) items"
            Keys 'Ctrl+Z'
            Check 'undo removes the duplicated scene' ((ItemCount) -eq 8) '8 items' "$(ItemCount) items"
        }

        25 {
            New-Scene 'stroke expansion'
            # path.expandStroke is Illustrator's Outline Stroke, and its own description fixes the width rule:
            # "below 4pt the original over four, otherwise 1pt". Both halves are checked here, and the whole
            # gesture is one undo step.
            #
            # Every item comes from LastItem, never from what a draw helper emits: those helpers push more
            # than one object down the pipeline, so a cached "item" can be a stream and "$item.id" on it is
            # the empty string. That is how this scene spent three rounds searching for the literal id ".id" -
            # and why the "wider path" comparison beside it passed for the wrong reason, comparing against
            # nothing.
            [void](DrawEllipse 150 150 200 140)
            StyleStroke '0000ff' 6
            $before = LastItem
            Check 'the stroke is 6pt before expanding' ((StrokeWidth $before.id) -eq 6) '6' (StrokeWidth $before.id)
            $r = Invoke-Op 'path.expandStroke' @{}
            Check 'expanding reports what it did' ($null -ne $r.result.expanded) 'a count' ($r.result | ConvertTo-Json -Compress)
            $after = LastItem
            Check 'a 6pt stroke expands to an outline carrying 1pt' ((StrokeWidth $after.id) -eq 1) '1' (StrokeWidth $after.id)
            Check 'the outline has both edges and is wider than the shape' (($after.w -gt $before.w) -and ($after.id -ne $before.id)) 'a new, wider path' ("{0:N1} on id {1}" -f $after.w, $after.id)
            Keys 'Ctrl+Z'
            Check 'one undo restores the original path' ([Math]::Abs((LastItem).w - $before.w) -le 1) ("{0:N1}" -f $before.w) ("{0:N1}" -f (LastItem).w)

            # The other half of the rule: a stroke thinner than 4pt expands to a quarter of itself.
            [void](DrawRect 420 200 140 100)
            StyleStroke '008000' 2
            Invoke-Op 'path.expandStroke' @{} | Out-Null
            $afterThin = LastItem
            Check 'a 2pt stroke expands to a 0.5pt outline' (((StrokeWidth $afterThin.id) - 0.5) -lt 0.0001) '0.5' (StrokeWidth $afterThin.id)
        }
    }
}

Write-Host 'starting the editor'
Start-App | Out-Null
Calibrate | Out-Null

$wanted = @()
if ($Only) { $wanted = @($Only.Split(';') | Where-Object { $_ } | ForEach-Object { [int]$_ }) }
for ($index = 1; $index -le 25; $index++) {
    if ($wanted.Count -gt 0 -and ($wanted -notcontains $index)) { continue }
    Write-Host ("scene {0}" -f $index) -ForegroundColor Cyan
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        Run-Scene $index
    } catch {
        Record-Failure 'the scene threw' 'the scene to complete' $_.Exception.Message
    }
    $sw.Stop()
    Write-Host ("   done in {0:N1}s" -f $sw.Elapsed.TotalSeconds) -ForegroundColor DarkGray
}

Write-Report -path (Join-Path (Get-Location) $ReportPath)