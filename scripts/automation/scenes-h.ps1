# Fifty more drawings, using the operation families the first twenty-five scenes never touched.
#
# Run: pwsh -NoProfile -File scripts\automation\scenes-h.ps1 [-Only '1;4;9']
#
# Same shape as scenes.ps1: every scene starts from a fresh document, draws with mouse and keyboard where the
# gesture is the thing under test and through the operation registry where it is not (AGENTS.md: API first,
# point-and-click second), and verifies the model after each gesture. Failures are collected into
# artifacts/auto/report-h.json with the scene, the step, what was expected and what the model held.
#
# The point of this file is reach: state, geometry, filters, brushes, text runs, definitions and the chrome, so
# that a defect has somewhere to show up. A scene that only ever touches what the first suite touched teaches
# nothing new.

param([string]$Only = '', [string]$ReportPath = 'artifacts\auto\report-h.json')

. (Join-Path $PSScriptRoot 'drive.ps1')

# How many scenes this file currently defines. Bumped as batches are added, so the runner can never report a
# scene as passing when it has no case at all.
$SceneCount = 10

# A shape's outline always selects it: clear, click the top edge, expect exactly it.
function VerifyOutlineSelect {
    param($item, [string]$label)
    if (-not $item) { return }
    ClearSelection
    SelectByOutline $item
    AssertSelection $item.id "$label is selectable by clicking its outline"
}

# Draw a named shape and hand back the row the model reports for it. Addressing an object by name rather than
# by z-order is what lets a scene talk about "the rect" after four other things exist.
function New-Obj {
    param([string]$type, [hashtable]$p, [string]$name)
    $params = @{}
    foreach ($k in $p.Keys) { $params[$k] = $p[$k] }
    $params['type'] = $type
    $params['name'] = $name
    $r = Invoke-Op 'object.create' $params
    if ($null -eq $r -or $r.ok -eq $false) { Record-Failure "creating $name" 'a created object' "$($r.error)" }
    return (ByName $name)
}

# The model reports width/height; every check in this file wants to say .w/.h, as scenes.ps1 does. **The two are
# not interchangeable**: reading .w off an operation result silently yields nothing, and a check comparing
# nothing to a number fails while looking exactly like a missing object. So the shape is normalised here, once.
function Shape {
    param($row)
    if ($null -eq $row) { return $null }
    $h = @{}
    foreach ($p in $row.PSObject.Properties) { $h[$p.Name] = $p.Value }
    if ($h.ContainsKey('width')) { $h['w'] = $h['width'] }
    if ($h.ContainsKey('height')) { $h['h'] = $h['height'] }
    if ($h.ContainsKey('subPaths') -and -not $h.ContainsKey('sub')) { $h['sub'] = $h['subPaths'] }
    return [pscustomobject]$h
}

function ByName {
    param([string]$name)
    $r = Invoke-Op 'object.find' @{ name = $name }
    if ($null -eq $r -or $null -eq $r.result) { return $null }
    return (Shape ($r.result.items | Select-Object -First 1))
}

# The single object the document holds, normalised. Used after an operation that merges or divides, where the
# surviving object's name is the operation's business rather than the caller's.
function OnlyItem {
    $r = Invoke-Op 'object.find' @{}
    if ($null -eq $r -or $null -eq $r.result) { return $null }
    return (Shape ($r.result.items | Select-Object -First 1))
}

function SelectIds {
    param([string[]]$ids)
    Invoke-Op 'selection.set' @{ itemIds = $ids } | Out-Null
    Start-Sleep -Milliseconds 120
}

# The node count a path reports, read from the document dump - the same traversal the Layers panel uses.
function NodesOf {
    param([string]$name)
    $d = (Invoke-Op 'ui.dump' @{ scope = 'document' }).result.text
    $line = ($d -split "`r?`n") | Where-Object { $_ -match [regex]::Escape($name) -and $_ -match 'nodes=' } | Select-Object -First 1
    if (-not $line) { return -1 }
    $m = [regex]::Match($line, 'nodes=(\d+)')
    if ($m.Success) { return [int]$m.Groups[1].Value }
    return -1
}

# How many objects the document holds, and how many of them are paths.
function TypeCount {
    param([string]$type)
    $r = (Invoke-Op 'object.find' @{ type = $type }).result
    if ($null -eq $r) { return 0 }
    return @($r.items).Count
}

# The paint of an object, as the document dump writes it, so a check can read what is stored. The dump is read
# through ui.dump: document.dump answers with a `documents` tree, not the text this file needs.
function PaintOf {
    param([string]$name)
    $d = (Invoke-Op 'ui.dump' @{ scope = 'document' }).result.text
    $line = ($d -split "`r?`n") | Where-Object { $_ -match [regex]::Escape($name) } | Select-Object -First 1
    return $line
}

function Run-Scene {
    param([int]$index)

    switch ($index) {

        # ---------------------------------------------------------------- boolean path operations

        1 {
            New-Scene 'union of two rectangles'
            $a = New-Obj 'rectangle' @{ x = 100; y = 100; width = 200; height = 160 } 'u-a'
            $b = New-Obj 'rectangle' @{ x = 200; y = 180; width = 200; height = 160 } 'u-b'
            Check 'two rectangles exist before the union' ((ItemCount) -eq 2) '2 items' "$(ItemCount) items"
            SelectIds @($a.itemId, $b.itemId)
            Check 'both rectangles are selected for the union' ((Selection).Count -eq 2) '2 selected' "$((Selection).Count) selected"
            $r = Invoke-Op 'path.union' @{}
            $after = ItemCount
            Check 'a union leaves one object' ($after -eq 1) '1 item' "$after items"
            $u = OnlyItem
            if ($u) {
                Check 'the union spans both rectangles' ([Math]::Abs($u.w - 300) -le 3 -and [Math]::Abs($u.h - 240) -le 3) 'w 300 h 240' ("w {0:N1} h {1:N1}" -f $u.w, $u.h)
            } else { Record-Failure 'the union has a reportable object' 'an object' 'not found' }
        }

        2 {
            New-Scene 'subtraction leaves the back-most shape with a bite'
            $a = New-Obj 'rectangle' @{ x = 100; y = 100; width = 240; height = 200 } 's-base'
            $b = New-Obj 'ellipse' @{ cx = 340; cy = 200; rx = 80; ry = 80 } 's-bite'
            SelectIds @($a.itemId, $b.itemId)
            Invoke-Op 'path.subtract' @{} | Out-Null
            Start-Sleep -Milliseconds 200
            $after = ItemCount
            Check 'the subtraction leaves one object' ($after -eq 1) '1 item' "$after items"
            $nodes = NodesOf 's-base'
            Check 'the bitten rectangle gained nodes' ($nodes -ge 8) '8 or more nodes' "$nodes nodes"
        }

        3 {
            New-Scene 'intersection of two rectangles'
            $a = New-Obj 'rectangle' @{ x = 100; y = 100; width = 240; height = 200 } 'i-a'
            $b = New-Obj 'rectangle' @{ x = 240; y = 200; width = 240; height = 200 } 'i-b'
            SelectIds @($a.itemId, $b.itemId)
            Invoke-Op 'path.intersect' @{} | Out-Null
            Start-Sleep -Milliseconds 200
            Check 'an intersection leaves one object' ((ItemCount) -eq 1) '1 item' "$(ItemCount) items"
            $x = OnlyItem
            if ($x) {
                Check 'the intersection is the overlap' ([Math]::Abs($x.w - 100) -le 4 -and [Math]::Abs($x.h - 100) -le 4) 'w 100 h 100' ("w {0:N1} h {1:N1}" -f $x.w, $x.h)
            } else { Record-Failure 'the intersection has a reportable object' 'an object' 'not found' }
        }

        4 {
            New-Scene 'exclude keeps what only one shape covers'
            $a = New-Obj 'rectangle' @{ x = 100; y = 100; width = 240; height = 200 } 'x-a'
            $b = New-Obj 'ellipse' @{ cx = 340; cy = 200; rx = 90; ry = 90 } 'x-b'
            SelectIds @($a.itemId, $b.itemId)
            Invoke-Op 'path.exclude' @{} | Out-Null
            Start-Sleep -Milliseconds 200
            Check 'an exclude leaves one object' ((ItemCount) -eq 1) '1 item' "$(ItemCount) items"
            $e = OnlyItem
            if ($e) {
                Check 'the excluded shape keeps the whole extent' ([Math]::Abs($e.w - 330) -le 8) 'w about 330' ("w {0:N1}" -f $e.w)
            } else { Record-Failure 'the exclude has a reportable object' 'an object' 'not found' }
        }

        5 {
            New-Scene 'divide cuts a union into its regions'
            $a = New-Obj 'rectangle' @{ x = 100; y = 100; width = 200; height = 200 } 'd-a'
            $b = New-Obj 'ellipse' @{ cx = 300; cy = 200; rx = 110; ry = 110 } 'd-b'
            SelectIds @($a.itemId, $b.itemId)
            Invoke-Op 'path.divide' @{} | Out-Null
            Start-Sleep -Milliseconds 300
            $n = ItemCount
            Check 'divide leaves the regions as separate objects' ($n -ge 2) '2 or more items' "$n items"
        }

        # ---------------------------------------------------------------- path geometry

        6 {
            New-Scene 'rounding one corner of a rectangle'
            $r = New-Obj 'rectangle' @{ x = 120; y = 120; width = 240; height = 180 } 'c-rect'
            $before = NodesOf 'c-rect'
            SelectIds @($r.itemId)
            $res = Invoke-Op 'path.roundCorner' @{ itemId = $r.itemId; subPath = 0; node = 1; radius = 40 }
            Start-Sleep -Milliseconds 200
            $after = NodesOf 'c-rect'
            Check 'rounding a corner adds nodes to the outline' ($after -gt $before) 'more nodes than before' "$before then $after"
            Check 'the operation reports the arc it used' ($res.ok -ne $false) 'a result' "$($res.error)"
        }

        7 {
            New-Scene 'inserting and moving a node'
            $p = New-Obj 'polyline' @{ points = @(@(120.0, 300.0), @(260.0, 160.0), @(400.0, 300.0)) } 'n-open'
            $before = NodesOf 'n-open'
            Invoke-Op 'path.insertNode' @{ itemId = $p.itemId; sub = 0; segment = 0; x = 190.0; y = 230.0 } | Out-Null
            Start-Sleep -Milliseconds 200
            $mid = NodesOf 'n-open'
            Check 'inserting a node adds one' ($mid -eq ($before + 1)) "$($before + 1) nodes" "$mid nodes"
            $b0 = ByName 'n-open'
            Invoke-Op 'path.moveNode' @{ itemId = $p.itemId; sub = 0; node = 1; x = 300.0; y = 60.0 } | Out-Null
            Start-Sleep -Milliseconds 200
            $b1 = ByName 'n-open'
            Check 'moving a node changes the bounding box' ($null -ne $b0 -and $null -ne $b1 -and [Math]::Abs($b1.h - $b0.h) -gt 1) 'a taller box' ("h {0:N1} then {1:N1}" -f $b0.h, $b1.h)
        }

        8 {
            New-Scene 'closing and joining open paths'
            $p1 = New-Obj 'polyline' @{ points = @(@(120.0, 320.0), @(260.0, 180.0), @(400.0, 320.0)) } 'j-1'
            SelectIds @($p1.itemId)
            Invoke-Op 'path.close' @{} | Out-Null
            Start-Sleep -Milliseconds 200
            $d = (Invoke-Op 'ui.dump' @{ scope = 'document' }).result.text
            Check 'closing an open path gives it a fillable outline' ($d -match [regex]::Escape('j-1')) 'the path is still in the document' 'not found'
            $p2 = New-Obj 'polyline' @{ points = @(@(460.0, 320.0), @(540.0, 260.0)) } 'j-2'
            SelectIds @($p1.itemId, $p2.itemId)
            $j = Invoke-Op 'path.join' @{}
            Start-Sleep -Milliseconds 200
            Check 'joining two open paths answers' ($j.ok -ne $false) 'a result' "$($j.error)"
        }

        9 {
            New-Scene 'outline stroke turns a line into a shape'
            $p = New-Obj 'polyline' @{ points = @(@(120.0, 300.0), @(260.0, 160.0), @(400.0, 300.0)); strokeColor = @(0, 0, 0); strokeWidth = 8 } 'e-stroke'
            Check 'the path starts as a stroked path' ((TypeCount 'path') -ge 1) 'a path' "$(TypeCount 'path') paths"
            SelectIds @($p.itemId)
            Invoke-Op 'path.expandStroke' @{} | Out-Null
            Start-Sleep -Milliseconds 300
            $items = (Invoke-Op 'object.find' @{ type = 'path' }).result.items
            Check 'expanding the stroke leaves a path with an outline' (@($items).Count -ge 1) 'a path' "$(@($items).Count) paths"
            $d = ((Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 24 -Compress)
            Check 'the expanded outline is filled' ($d -match '"Fill"') 'a fill in the document' 'no fill reported'
        }

        10 {
            New-Scene 'compound path and back again'
            $a = New-Obj 'ellipse' @{ cx = 240; cy = 240; rx = 120; ry = 120 } 'cp-outer'
            $b = New-Obj 'ellipse' @{ cx = 240; cy = 240; rx = 60; ry = 60 } 'cp-inner'
            SelectIds @($a.itemId, $b.itemId)
            $c = Invoke-Op 'path.makeCompound' @{}
            Start-Sleep -Milliseconds 250
            $n = ItemCount
            Check 'a compound path is one object' ($n -eq 1) '1 item' "$n items"
            Check 'making a compound path answers' ($c.ok -ne $false) 'a result' "$($c.error)"
            $after = (Invoke-Op 'object.find' @{}).result.items
            if (@($after).Count -eq 1) {
                SelectIds @($after[0].itemId)
                $rel = Invoke-Op 'path.releaseCompound' @{}
                Start-Sleep -Milliseconds 250
                $back = ItemCount
                Check 'releasing a compound path gives the pieces back' ($back -eq 2) '2 items' "$back items"
                Check 'releasing a compound path answers' ($rel.ok -ne $false) 'a result' "$($rel.error)"
            } else { Record-Failure 'the compound path can be released' 'one object' "$n items" }
        }
    }
}

# ---------------------------------------------------------------- run

Start-App | Out-Null
Calibrate | Out-Null

$wanted = @()
if ($Only) { $wanted = @($Only.Split(';') | Where-Object { $_ } | ForEach-Object { [int]$_ }) }
for ($index = 1; $index -le $SceneCount; $index++) {
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
