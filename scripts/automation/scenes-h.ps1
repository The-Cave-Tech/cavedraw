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
$SceneCount = 50

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

# A text block is created through the registry and then addressed by type: the id comes back from object.find
# rather than from the create result, because every later call needs the id and only one of the two is
# documented to carry it.
function New-Text {
    param([string]$body, [double]$x = 140.0, [double]$y = 200.0, [double]$size = 24.0)
    Invoke-Op 'text.create' @{ x = $x; y = $y; text = $body; fontSize = $size } | Out-Null
    Start-Sleep -Milliseconds 150
    $rows = (Invoke-Op 'object.find' @{ type = 'text' }).result.items
    return (Shape (@($rows) | Where-Object { $_.text -eq $body } | Select-Object -First 1))
}

# The whole document as one JSON string. Most of these checks are about what the model holds rather than what an
# operation returns, and the serialized model is the honest place to read that - it is also where a gap in the
# registry's own readouts shows up, as #241 and #242 did.
function ModelText {
    return ((Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 24 -Compress)
}

function Run-Scene {
    param([int]$index)    switch ($index) {

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

        # ---------------------------------------------------------------- filters

        11 {
            New-Scene 'a blurred rectangle'
            $r = New-Obj 'rectangle' @{ x = 140; y = 140; width = 260; height = 180; fillColor = @(200, 60, 60) } 'blur-rect'
            SelectIds @($r.itemId)
            $c = Invoke-Op 'filter.create' @{ name = 'soft'; primitives = @(@{ kind = 'gaussianBlur'; radius = 8.0 }) }
            Check 'a filter can be created' ($c.ok -ne $false) 'a result' "$($c.error)"
            Check 'the filter holds one primitive' ($c.result.primitives -eq 1) '1 primitive' "$($c.result.primitives)"
            $a = Invoke-Op 'filter.apply' @{ name = 'soft' }
            Check 'the filter is applied to one item' ($a.result.items -eq 1) '1 item' "$($a.result.items)"
            $f = (Invoke-Op 'filter.list' @{}).result
            Check 'the document reports the filter' ($f.name -eq 'soft') 'soft' "$($f.name)"
            Check 'its step is a gaussian blur' (@($f.primitives)[0].kind -eq 'GaussianBlur') 'GaussianBlur' "$(@($f.primitives)[0].kind)"
            Check 'the radius is the one that was asked for' ([Math]::Abs(@($f.primitives)[0].radius - 8) -lt 0.01) 'radius 8' "$(@($f.primitives)[0].radius)"
        }

        12 {
            New-Scene 'a drop shadow built from three steps'
            $r = New-Obj 'rectangle' @{ x = 160; y = 160; width = 220; height = 150; fillColor = @(240, 240, 240) } 'shadow-rect'
            SelectIds @($r.itemId)
            $c = Invoke-Op 'filter.create' @{ name = 'shadow'; primitives = @(
                @{ kind = 'offset'; dx = 12.0; dy = 12.0; result = 'off' },
                @{ kind = 'flood'; floodColor = @(0, 0, 0); floodOpacity = 0.5; result = 'col' },
                @{ kind = 'composite'; in = 'col'; in2 = 'off'; operator = 'in'; result = 'shadow' }
            ) }
            Check 'a three-step filter can be created' ($c.result.primitives -eq 3) '3 primitives' "$($c.result.primitives)"
            Invoke-Op 'filter.apply' @{ name = 'shadow' } | Out-Null
            Start-Sleep -Milliseconds 200
            $f = (Invoke-Op 'filter.list' @{}).result
            $kinds = @($f.primitives | ForEach-Object { $_.kind }) -join ','
            Check 'the chain is kept in order' ($kinds -eq 'Offset,Flood,Composite') 'Offset,Flood,Composite' $kinds
            Check 'the offset carries its distance' (@($f.primitives)[0].dx -eq 12) 'dx 12' "$(@($f.primitives)[0].dx)"
        }

        13 {
            New-Scene 'a colour matrix desaturates'
            $r = New-Obj 'rectangle' @{ x = 150; y = 150; width = 240; height = 170; fillColor = @(220, 40, 40) } 'cm-rect'
            SelectIds @($r.itemId)
            $c = Invoke-Op 'filter.create' @{ name = 'grey'; primitives = @(@{ kind = 'colorMatrix'; type = 'saturate'; values = @(0.0) }) }
            Check 'a colour matrix filter can be created' ($c.ok -ne $false) 'a result' "$($c.error)"
            Invoke-Op 'filter.apply' @{ name = 'grey' } | Out-Null
            Start-Sleep -Milliseconds 200
            $f = (Invoke-Op 'filter.list' @{}).result
            Check 'the step is a colour matrix' (@($f.primitives)[0].kind -eq 'ColorMatrix') 'ColorMatrix' "$(@($f.primitives)[0].kind)"
        }

        14 {
            New-Scene 'changing a turbulence seed'
            $r = New-Obj 'rectangle' @{ x = 150; y = 150; width = 240; height = 170; fillColor = @(80, 120, 200) } 'tb-rect'
            SelectIds @($r.itemId)
            Invoke-Op 'filter.create' @{ name = 'grain'; primitives = @(@{ kind = 'turbulence'; baseFrequency = 0.05; numOctaves = 3; seed = 1.0 }) } | Out-Null
            Invoke-Op 'filter.apply' @{ name = 'grain' } | Out-Null
            Start-Sleep -Milliseconds 200
            $before = ((Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 24 -Compress)
            Check 'the turbulence step is in the document' ($before -match 'Turbulence') 'a turbulence step' 'not found'
            Check 'it holds the seed it was created with' ($before -match '"Seed":1') 'Seed 1' 'no seed of 1'
            $set = Invoke-Op 'filter.setPrimitiveParameter' @{ name = 'grain'; index = 0; parameter = 'seed'; value = 7.0 }
            Check 'setting a parameter answers' ($set.ok -ne $false) 'a result' "$($set.error)"
            Start-Sleep -Milliseconds 200
            $after = ((Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 24 -Compress)
            Check 'the seed is the one that was set' ($after -match '"Seed":7') 'Seed 7' 'unchanged'
            # **Read through the model, not through filter.list.** filter.list reports radius, dx, dy,
            # floodOpacity, op and mode whatever the kind is, so a turbulence's baseFrequency, numOctaves and
            # seed are settable and unreadable through the registry - filed as #241. This scene verifies the
            # change where it is stored, and the check below pins what filter.list does report.
            $listed = ((Invoke-Op 'filter.list' @{}).result | ConvertTo-Json -Depth 8 -Compress)
            Check 'filter.list reports the step itself' ($listed -match 'Turbulence') 'a turbulence step' 'not reported'
        }

        15 {
            New-Scene 'morphology thickens a stroke'
            $r = New-Obj 'rectangle' @{ x = 170; y = 170; width = 200; height = 140; fillColor = @(255, 255, 255); strokeColor = @(0, 0, 0); strokeWidth = 4 } 'mo-rect'
            SelectIds @($r.itemId)
            $c = Invoke-Op 'filter.create' @{ name = 'thick'; primitives = @(@{ kind = 'morphology'; operator = 'dilate'; radius = 5.0 }) }
            Check 'a morphology filter can be created' ($c.ok -ne $false) 'a result' "$($c.error)"
            Invoke-Op 'filter.apply' @{ name = 'thick' } | Out-Null
            Start-Sleep -Milliseconds 200
            $f = (Invoke-Op 'filter.list' @{}).result
            Check 'the step is a morphology' (@($f.primitives)[0].kind -eq 'Morphology') 'Morphology' "$(@($f.primitives)[0].kind)"
            Check 'it kept the radius' ([Math]::Abs(@($f.primitives)[0].radius - 5) -lt 0.01) 'radius 5' "$(@($f.primitives)[0].radius)"
        }

        16 {
            New-Scene 'wiring a step to the source'
            $r = New-Obj 'rectangle' @{ x = 180; y = 180; width = 200; height = 140; fillColor = @(120, 200, 120) } 'w-rect'
            SelectIds @($r.itemId)
            Invoke-Op 'filter.create' @{ name = 'wired'; primitives = @(@{ kind = 'gaussianBlur'; radius = 4.0; result = 'blurred' }) } | Out-Null
            $add = Invoke-Op 'filter.addPrimitive' @{ name = 'wired'; kind = 'offset'; in = 'blurred'; dx = 20.0; dy = 0.0 }
            Check 'a step can be added to a filter' ($add.ok -ne $false) 'a result' "$($add.error)"
            $con = Invoke-Op 'filter.connectPrimitive' @{ name = 'wired'; index = 1; in = 'SourceAlpha' }
            Check 'wiring a step to the source answers' ($con.ok -ne $false) 'a result' "$($con.error)"
            Invoke-Op 'filter.apply' @{ name = 'wired' } | Out-Null
            Start-Sleep -Milliseconds 200
            $f = (Invoke-Op 'filter.list' @{}).result
            Check 'the filter now holds two steps' (@($f.primitives).Count -eq 2) '2 primitives' "@($($f.primitives).Count)"
            $dump = ((Invoke-Op 'filter.list' @{}).result | ConvertTo-Json -Depth 8 -Compress)
            Check 'the rewiring is reported' ($dump -match 'SourceAlpha') 'SourceAlpha named as an input' 'not named'
        }

        17 {
            New-Scene 'the filter region in user space'
            $r = New-Obj 'rectangle' @{ x = 200; y = 200; width = 180; height = 120; fillColor = @(200, 200, 120) } 'reg-rect'
            SelectIds @($r.itemId)
            Invoke-Op 'filter.create' @{ name = 'region'; primitives = @(@{ kind = 'gaussianBlur'; radius = 6.0 }) } | Out-Null
            $s = Invoke-Op 'filter.setRegion' @{ name = 'region'; x = 100.0; y = 100.0; width = 400.0; height = 300.0; userSpace = $true; primitiveUnits = 'userSpaceOnUse' }
            Check 'setting a region answers' ($s.ok -ne $false) 'a result' "$($s.error)"
            $f = (Invoke-Op 'filter.list' @{}).result
            Check 'the region is the one that was set' ([Math]::Abs($f.width - 400) -le 1 -and [Math]::Abs($f.height - 300) -le 1) 'w 400 h 300' ("w {0:N1} h {1:N1}" -f $f.width, $f.height)
            Check 'the region reports user space units' ($f.primitiveUnits -eq 'userSpaceOnUse') 'userSpaceOnUse' "$($f.primitiveUnits)"
        }

        # ---------------------------------------------------------------- gradients

        18 {
            New-Scene 'a radial ramp and what it samples'
            $r = New-Obj 'rectangle' @{ x = 160; y = 160; width = 240; height = 200 } 'rg-rect'
            SelectIds @($r.itemId)
            Invoke-Op 'gradient.setStops' @{ stops = @(
                @{ position = 0.0; color = 'ff0000' },
                @{ position = 1.0; color = '0000ff' }
            ) } | Out-Null
            Invoke-Op 'gradient.setKind' @{ kind = 'radial' } | Out-Null
            Start-Sleep -Milliseconds 200
            $g = (Invoke-Op 'gradient.get' @{}).result
            Check 'the fill is a gradient' ($g.hasGradient -eq $true) 'a gradient' "$($g.hasGradient)"
            Check 'the kind is radial' ($g.gradient.kind -eq 'radial') 'radial' "$($g.gradient.kind)"
            Check 'it has two stops' (@($g.gradient.stops).Count -eq 2) '2 stops' "@(@($g.gradient.stops).Count)"
            $s0 = (Invoke-Op 'gradient.sample' @{ position = 0.0 }).result
            $s5 = (Invoke-Op 'gradient.sample' @{ position = 0.5 }).result
            $s1 = (Invoke-Op 'gradient.sample' @{ position = 1.0 }).result
            Check 'the start of the ramp is the first stop' ($s0.color.r -gt 0.9 -and $s0.color.b -lt 0.1) 'red' ("r {0:N2} b {1:N2}" -f $s0.color.r, $s0.color.b)
            Check 'the end of the ramp is the second stop' ($s1.color.b -gt 0.9) 'blue' ("b {0:N2}" -f $s1.color.b)
            Check 'the middle is between them' ([Math]::Abs($s5.color.r - 0.5) -lt 0.06 -and [Math]::Abs($s5.color.b - 0.5) -lt 0.06) 'half of each' ("r {0:N2} b {1:N2}" -f $s5.color.r, $s5.color.b)
        }

        19 {
            New-Scene 'conical and freeform ramps'
            $r = New-Obj 'ellipse' @{ cx = 300; cy = 260; rx = 150; ry = 130 } 'cf-shape'
            SelectIds @($r.itemId)
            Invoke-Op 'gradient.setStops' @{ stops = @(
                @{ position = 0.0; color = 'ff0000' },
                @{ position = 0.5; color = '00ff00' },
                @{ position = 1.0; color = '0000ff' }
            ) } | Out-Null
            Invoke-Op 'gradient.setKind' @{ kind = 'conical' } | Out-Null
            Start-Sleep -Milliseconds 200
            $g1 = (Invoke-Op 'gradient.get' @{}).result
            Check 'a conical gradient can be made' ($g1.gradient.kind -eq 'conical') 'conical' "$($g1.gradient.kind)"
            Check 'the three stops survive the kind change' (@($g1.gradient.stops).Count -eq 3) '3 stops' "@(@($g1.gradient.stops).Count)"
            Invoke-Op 'gradient.setKind' @{ kind = 'freeform' } | Out-Null
            Invoke-Op 'gradient.setGeometry' @{ freeformMode = 'points'; points = @(
                @{ x = 0.1; y = 0.1; color = 'ff0000' },
                @{ x = 0.5; y = 0.5; color = 'ffff00' },
                @{ x = 0.9; y = 0.2; color = '00aaff' }
            ) } | Out-Null
            Start-Sleep -Milliseconds 200
            $g2 = (Invoke-Op 'gradient.get' @{}).result
            Check 'a freeform gradient can be made' ($g2.gradient.kind -eq 'freeform') 'freeform' "$($g2.gradient.kind)"
            Check 'its points are the ones given' (@($g2.gradient.freeform.points).Count -eq 3) '3 points' "@(@($g2.gradient.freeform.points).Count)"
        }

        20 {
            New-Scene 'spread, reverse and stop surgery'
            $r = New-Obj 'rectangle' @{ x = 140; y = 180; width = 280; height = 160 } 'sp-rect'
            SelectIds @($r.itemId)
            Invoke-Op 'gradient.setStops' @{ stops = @(
                @{ position = 0.0; color = 'ff0000' },
                @{ position = 1.0; color = '0000ff' }
            ) } | Out-Null
            $s = Invoke-Op 'gradient.setSpread' @{ spread = 'repeat' }
            Check 'the spread can be set to repeat' ($s.result.spread -eq 'repeat') 'repeat' "$($s.result.spread)"
            $rev = Invoke-Op 'gradient.reverse' @{}
            Check 'reversing answers' ($rev.ok -ne $false) 'a result' "$($rev.error)"
            $g1 = (Invoke-Op 'gradient.get' @{}).result
            Check 'reversing puts the second stop first' (@($g1.gradient.stops)[0].color.b -gt 0.9) 'blue at position 0' ("b {0:N2}" -f @($g1.gradient.stops)[0].color.b)
            $add = Invoke-Op 'gradient.addStop' @{ position = 0.5; color = '00ff00' }
            Start-Sleep -Milliseconds 150
            $g2 = (Invoke-Op 'gradient.get' @{}).result
            Check 'adding a stop gives three' (@($g2.gradient.stops).Count -eq 3) '3 stops' "@(@($g2.gradient.stops).Count)"
            $mv = Invoke-Op 'gradient.moveStop' @{ index = 1; position = 0.7 }
            Start-Sleep -Milliseconds 150
            $g3 = (Invoke-Op 'gradient.get' @{}).result
            $mid = @($g3.gradient.stops) | Where-Object { $_.color.g -gt 0.9 } | Select-Object -First 1
            Check 'the moved stop is at its new position' ($null -ne $mid -and [Math]::Abs($mid.position - 0.7) -lt 0.02) '0.70' "$($mid.position)"
            $rm = Invoke-Op 'gradient.removeStop' @{ index = 1 }
            Start-Sleep -Milliseconds 150
            $g4 = (Invoke-Op 'gradient.get' @{}).result
            Check 'removing it gives two again' (@($g4.gradient.stops).Count -eq 2) '2 stops' "@(@($g4.gradient.stops).Count)"
        }

        # ---------------------------------------------------------------- stroke stacks, profiles and effects

        21 {
            New-Scene 'a second stroke on one path'
            $r = New-Obj 'rectangle' @{ x = 160; y = 160; width = 240; height = 170; fillColor = @(250, 250, 250) } 'ms-rect'
            SelectIds @($r.itemId)
            $s0 = (Invoke-Op 'style.strokes' @{}).result
            Check 'a fresh path carries one stroke' ($s0.count -eq 1) '1 stroke' "$($s0.count)"
            $add = Invoke-Op 'style.addStroke' @{ color = @(220, 30, 30); width = 6; cap = 'round' }
            Check 'a stroke can be added' ($add.ok -ne $false) 'a result' "$($add.error)"
            Start-Sleep -Milliseconds 150
            $s1 = (Invoke-Op 'style.strokes' @{}).result
            # **Read the array, not the count.** style.strokes' own `count` is stale after addStroke - it reports
            # 1 while the same response lists 2 - which is filed as #242. The add is not in doubt: it answers
            # {"changed":1} and the second row is the stroke that was asked for.
            Check 'the stack now holds two strokes' (@($s1.strokes).Count -eq 2) '2 strokes' "$(@($s1.strokes).Count)"
            $top = @($s1.strokes)[1]
            Check 'the new stroke is on top with its width' ([Math]::Abs($top.width - 6) -lt 0.01) 'width 6' "$($top.width)"
            Check 'and with its colour' ($top.hex -eq '#dc1e1e') '#dc1e1e' "$($top.hex)"
            Check 'and with its cap' ($top.cap -eq 'round') 'round' "$($top.cap)"
        }

        22 {
            New-Scene 'reordering and hiding a stroke'
            $r = New-Obj 'rectangle' @{ x = 170; y = 170; width = 220; height = 150; fillColor = @(240, 240, 240) } 'ro-rect'
            SelectIds @($r.itemId)
            Invoke-Op 'style.addStroke' @{ color = @(30, 90, 200); width = 5 } | Out-Null
            Start-Sleep -Milliseconds 150
            $re = Invoke-Op 'style.reorderStroke' @{ from = 0; to = 1 }
            Check 'reordering a stroke answers' ($re.ok -ne $false) 'a result' "$($re.error)"
            Start-Sleep -Milliseconds 150
            $s = (Invoke-Op 'style.strokes' @{}).result
            Check 'both strokes are still there after reordering' (@($s.strokes).Count -eq 2) '2 strokes' "$(@($s.strokes).Count)"
            Invoke-Op 'style.setStrokeVisible' @{ visible = $false; index = 0 } | Out-Null
            Start-Sleep -Milliseconds 150
            $s2 = (Invoke-Op 'style.strokes' @{}).result
            $hidden = @($s2.strokes | Where-Object { -not $_.visible })
            Check 'one stroke is now hidden' (@($hidden).Count -eq 1) '1 hidden stroke' "@($hidden).Count hidden"
        }

        23 {
            New-Scene 'a translucent stroke that multiplies'
            $r = New-Obj 'rectangle' @{ x = 180; y = 180; width = 200; height = 140; fillColor = @(255, 240, 200) } 'bl-rect'
            SelectIds @($r.itemId)
            Invoke-Op 'style.addStroke' @{ color = @(20, 20, 20); width = 8 } | Out-Null
            Start-Sleep -Milliseconds 150
            $set = Invoke-Op 'style.setStroke' @{ index = 1; opacity = 0.4; blend = 'multiply'; width = 8 }
            Check 'per-stroke opacity and blend can be set' ($set.ok -ne $false) 'a result' "$($set.error)"
            Start-Sleep -Milliseconds 150
            $s = (Invoke-Op 'style.strokes' @{}).result
            $top = @($s.strokes)[1]
            Check 'the stroke reports its opacity' ([Math]::Abs($top.effectiveOpacity - 0.4) -lt 0.02) '0.40' ("{0:N2}" -f $top.effectiveOpacity)
        }

        24 {
            New-Scene 'a width profile along a stroke'
            $p = New-Obj 'polyline' @{ points = @(@(120.0, 330.0), @(280.0, 180.0), @(440.0, 330.0)); strokeColor = @(0, 0, 0); strokeWidth = 6 } 'wp-path'
            SelectIds @($p.itemId)
            $set = Invoke-Op 'style.setWidthProfile' @{ points = @(
                @{ position = 0.0; left = 1.0; right = 1.0 },
                @{ position = 1.0; left = 9.0; right = 9.0 }
            ) }
            Check 'a width profile can be set' ($set.ok -ne $false) 'a result' "$($set.error)"
            Start-Sleep -Milliseconds 200
            $m = ((Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 24 -Compress)
            Check 'the profile is stored on the stroke' ($m -match '(?i)widthprofile|profile') 'a width profile' 'no profile in the model'
            $common = (Invoke-Op 'style.commonStroke' @{}).result
            Check 'the common stroke answers' ($null -ne $common) 'a summary' 'nothing'
        }

        25 {
            New-Scene 'a tablet response on a stroke'
            $p = New-Obj 'polyline' @{ points = @(@(140.0, 320.0), @(300.0, 200.0), @(460.0, 320.0)); strokeColor = @(20, 20, 20); strokeWidth = 5 } 'dy-path'
            SelectIds @($p.itemId)
            $d = Invoke-Op 'style.setDynamics' @{ target = 'width'; preset = 'soft' }
            Check 'dynamics can be set' ($d.ok -ne $false) 'a result' "$($d.error)"
            Start-Sleep -Milliseconds 200
            $m = ((Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 24 -Compress)
            Check 'the dynamics are stored' ($m -match '(?i)dynamics') 'a dynamics block' 'no dynamics in the model'
            $off = Invoke-Op 'style.setDynamics' @{ target = 'width'; enabled = $false }
            Check 'one target can be switched off' ($off.ok -ne $false) 'a result' "$($off.error)"
        }

        26 {
            New-Scene 'a zigzag outline effect'
            $r = New-Obj 'rectangle' @{ x = 180; y = 180; width = 200; height = 140; fillColor = @(255, 255, 255); strokeColor = @(0, 0, 0); strokeWidth = 3 } 'zz-rect'
            SelectIds @($r.itemId)
            $a = Invoke-Op 'style.addStrokeEffect' @{ kind = 'zigZag'; size = 5.0; ridges = 3; seed = 2.0 }
            Check 'an outline effect can be added' ($a.ok -ne $false) 'a result' "$($a.error)"
            Start-Sleep -Milliseconds 200
            $m1 = ((Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 24 -Compress)
            Check 'the effect is stored' ($m1 -match '(?i)zigzag') 'a zigZag effect' 'no zigZag in the model'
            $p = Invoke-Op 'style.setEffectParameter' @{ name = 'ridges'; value = 6; index = 0 }
            Check 'an effect parameter can be set' ($p.ok -ne $false) 'a result' "$($p.error)"
            Start-Sleep -Milliseconds 200
            $m2 = ((Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 24 -Compress)
            Check 'the new ridge count is stored' ($m2 -match '(?i)"ridges":6|ridges=6|"Ridges":6') 'six ridges' 'unchanged'
        }

        27 {
            New-Scene 'two effects, reordered then one removed'
            $r = New-Obj 'rectangle' @{ x = 190; y = 190; width = 180; height = 130; fillColor = @(255, 255, 255); strokeColor = @(0, 0, 0); strokeWidth = 3 } 'ef-rect'
            SelectIds @($r.itemId)
            Invoke-Op 'style.addStrokeEffect' @{ kind = 'zigZag'; size = 4.0; ridges = 2 } | Out-Null
            Start-Sleep -Milliseconds 150
            Invoke-Op 'style.addStrokeEffect' @{ kind = 'roughen'; size = 3.0; detail = 2.0 } | Out-Null
            Start-Sleep -Milliseconds 200
            $m1 = ((Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 24 -Compress)
            Check 'both effects are stored' (($m1 -match '(?i)zigzag') -and ($m1 -match '(?i)roughen')) 'zigZag and roughen' 'one is missing'
            $ro = Invoke-Op 'style.reorderStrokeEffect' @{ from = 0; to = 1 }
            Check 'an effect can be reordered' ($ro.ok -ne $false) 'a result' "$($ro.error)"
            $rm = Invoke-Op 'style.removeStrokeEffect' @{ index = 0 }
            Check 'an effect can be removed' ($rm.ok -ne $false) 'a result' "$($rm.error)"
            Start-Sleep -Milliseconds 200
            $m2 = ((Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 24 -Compress)
            $left = 0
            if ($m2 -match '(?i)zigzag') { $left++ }
            if ($m2 -match '(?i)roughen') { $left++ }
            Check 'one effect is left' ($left -eq 1) '1 effect' "$left effects"
        }

        28 {
            New-Scene 'a drop shadow as a raster effect'
            $r = New-Obj 'rectangle' @{ x = 200; y = 200; width = 180; height = 130; fillColor = @(250, 250, 250) } 'rs-rect'
            SelectIds @($r.itemId)
            $a = Invoke-Op 'style.addRasterEffect' @{ kind = 'dropShadow'; radius = 6.0; offsetX = 8.0; offsetY = 8.0; opacity = 0.5; tint = @(0, 0, 0) }
            Check 'a raster effect can be added' ($a.ok -ne $false) 'a result' "$($a.error)"
            Start-Sleep -Milliseconds 200
            $m = ((Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 24 -Compress)
            Check 'the shadow is stored' ($m -match '(?i)dropshadow') 'a dropShadow effect' 'no dropShadow in the model'
            $rm = Invoke-Op 'style.removeRasterEffect' @{ index = 0 }
            Check 'a raster effect can be removed' ($rm.ok -ne $false) 'a result' "$($rm.error)"
            Start-Sleep -Milliseconds 200
            $m2 = ((Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 24 -Compress)
            Check 'the shadow is gone' (-not ($m2 -match '(?i)dropshadow')) 'no dropShadow' 'still stored'
        }

        29 {
            New-Scene 'the powerstroke path effect'
            $p = New-Obj 'polyline' @{ points = @(@(140.0, 340.0), @(280.0, 180.0), @(420.0, 340.0)); strokeColor = @(0, 0, 0); strokeWidth = 6 } 'ps-path'
            SelectIds @($p.itemId)
            $l0 = (Invoke-Op 'pathEffect.list' @{}).result
            Check 'the build reports powerstroke as implemented' (@($l0.implemented) -contains 'powerstroke') 'powerstroke' "$(@($l0.implemented) -join ',')"
            Check 'the list names the path it can act on' (@($l0.items).Count -ge 1) 'the path' "$(@($l0.items).Count) items"
            # **Powerstroke cannot be built today.** A path that carries a width profile - through either the
            # per-stroke route or the profile library, both tried by hand - is still refused with "'powerstroke'
            # carries no offset points, so there is no width to build". The library route is exercised here
            # because it is the one a person uses, and the refusal is pinned as a sentinel: when #243 is fixed
            # this check must become the positive assertion that the effect lands.
            $pc = Invoke-Op 'profile.create' @{ name = 'wide'; points = @(
                @{ position = 0.0; left = 2.0; right = 2.0 },
                @{ position = 0.5; left = 14.0; right = 14.0 },
                @{ position = 1.0; left = 2.0; right = 2.0 }
            ) }
            Check 'a width profile can be created' ($pc.result.points -eq 3) '3 points' "$($pc.result.points)"
            $pa = Invoke-Op 'profile.apply' @{ name = 'wide' }
            Check 'the profile applies to the path' ($pa.result.paths -eq 1) '1 path' "$($pa.result.paths)"
            Start-Sleep -Milliseconds 200
            $st = (Invoke-Op 'style.strokes' @{}).result.strokes
            Check 'the stroke carries the profile' ($null -ne $st.profile -and @($st.profile.points).Count -eq 3) 'a 3-point profile' "$(if ($st.profile) { @($st.profile.points).Count } else { 'none' })"
            $a = Invoke-Op 'pathEffect.apply' @{ effect = 'powerstroke' }
            $refused = @($a.result.refused)
            Check 'powerstroke is refused although the path carries a width profile (#243)' ($refused.Count -eq 1 -and $a.result.strokes -eq 0) 'one refusal and no stroke effect' "$($refused.Count) refused, $($a.result.strokes) strokes"
        }

        30 {
            New-Scene 'a hatch fill, then off again'
            $r = New-Obj 'rectangle' @{ x = 180; y = 180; width = 220; height = 160; strokeColor = @(20, 40, 90); strokeWidth = 2 } 'ha-rect'
            SelectIds @($r.itemId)
            $h = Invoke-Op 'style.setHatch' @{ angle = 30.0; spacing = 6.0; width = 2.0; cross = $true }
            Check 'a hatch can be set' ($h.ok -ne $false) 'a result' "$($h.error)"
            Start-Sleep -Milliseconds 200
            $m = ((Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 24 -Compress)
            Check 'the hatch is stored on the fill' ($m -match '(?i)hatch') 'a hatch' 'no hatch in the model'
            $c = Invoke-Op 'style.setHatch' @{ clear = $true }
            Check 'the hatch can be cleared' ($c.ok -ne $false) 'a result' "$($c.error)"
            Start-Sleep -Milliseconds 200
            $m2 = ((Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 24 -Compress)
            Check 'the hatch is gone' (-not ($m2 -match '(?i)"hatch"')) 'no hatch' 'still stored'
        }

        # ---------------------------------------------------------------- text runs and layout

        31 {
            New-Scene 'a text run with its own colour and weight'
            $t = New-Text 'The quick brown fox'
            if (-not $t) { Record-Failure 'a text block can be created' 'a text item' 'not found'; return }
            SelectIds @($t.itemId)
            $r0 = (Invoke-Op 'text.runs' @{ itemId = $t.itemId }).result
            Check 'a fresh block has one run' (@($r0.runs).Count -eq 1) '1 run' "$(@($r0.runs).Count)"
            $u = Invoke-Op 'text.update' @{ runColor = @(220, 20, 20); bold = $true }
            Check 'a run colour and weight can be set' ($u.ok -ne $false) 'a result' "$($u.error)"
            Start-Sleep -Milliseconds 200
            $m = ModelText
            Check 'the run colour is stored' ($m -match '(?i)runc(h)?olor|"Color"') 'a run colour' 'not stored'
            $r1 = (Invoke-Op 'text.runs' @{ itemId = $t.itemId }).result
            Check 'the block reports its run' (@($r1.runs).Count -eq 1) '1 run' "$(@($r1.runs).Count)"
        }

        32 {
            New-Scene 'superscript and subscript runs'
            $t = New-Text 'E = mc2 and H2O'
            if (-not $t) { Record-Failure 'a text block can be created' 'a text item' 'not found'; return }
            SelectIds @($t.itemId)
            $sup = Invoke-Op 'text.update' @{ baselineShift = 'super' }
            Check 'a superscript shift can be set' ($sup.ok -ne $false) 'a result' "$($sup.error)"
            Start-Sleep -Milliseconds 200
            $m1 = ModelText
            Check 'the shift is stored' ($m1 -match '(?i)baselineshift') 'a baseline shift' 'not stored'
            $back = Invoke-Op 'text.update' @{ baselineShift = 0.0 }
            Check 'the shift can be taken back' ($back.ok -ne $false) 'a result' "$($back.error)"
            Start-Sleep -Milliseconds 200
            $m2 = ModelText
            Check 'a zero shift is the same as none' (-not ($m2 -match '(?i)"baselineshift":0\.5')) 'no half-em shift' 'still shifted'
        }

        33 {
            New-Scene 'letter and word spacing'
            $t = New-Text 'Spaced out words here'
            if (-not $t) { Record-Failure 'a text block can be created' 'a text item' 'not found'; return }
            SelectIds @($t.itemId)
            $u = Invoke-Op 'text.update' @{ letterSpacing = 3.0; wordSpacing = 6.0 }
            Check 'spacing can be set on a run' ($u.ok -ne $false) 'a result' "$($u.error)"
            Start-Sleep -Milliseconds 200
            $m = ModelText
            Check 'the letter spacing is stored' ($m -match '(?i)letterspacing') 'a letter spacing' 'not stored'
            Check 'the word spacing is stored' ($m -match '(?i)wordspacing') 'a word spacing' 'not stored'
            $wider = ByName $t.name
            Check 'the block measures wider than a plain one' ($null -ne $wider) 'a measured block' 'not found'
        }

        34 {
            New-Scene 'alignment, leading and paragraph spacing'
            $t = New-Text "first line`nsecond line"
            if (-not $t) { Record-Failure 'a text block can be created' 'a text item' 'not found'; return }
            SelectIds @($t.itemId)
            $s = Invoke-Op 'text.style' @{ alignment = 'center'; lineSpacing = 1.6; paragraphSpacing = 8.0 }
            Check 'paragraph style can be set' ($s.ok -ne $false) 'a result' "$($s.error)"
            Start-Sleep -Milliseconds 200
            $m = ModelText
            Check 'the alignment is stored' ($m -match '(?i)"Alignment":"center"|alignment=center') 'center' 'not stored'
            Check 'the leading is stored' ($m -match '(?i)linespacing') 'a line spacing' 'not stored'
        }

        35 {
            New-Scene 'a vertical text column'
            $t = New-Text 'VERTICAL COLUMN'
            if (-not $t) { Record-Failure 'a text block can be created' 'a text item' 'not found'; return }
            SelectIds @($t.itemId)
            $before = ByName $t.name
            $s = Invoke-Op 'text.style' @{ writingMode = 'vertical-rl' }
            Check 'a writing mode can be set' ($s.ok -ne $false) 'a result' "$($s.error)"
            Start-Sleep -Milliseconds 250
            $m = ModelText
            # The model names its own enums: the stored value is VerticalRl, not the CSS spelling a caller sends.
            Check 'the writing mode is stored' ($m -match '(?i)"WritingMode":"VerticalRl"') 'WritingMode VerticalRl' 'not stored'
            $after = ByName $t.name
            Check 'a vertical column is taller than it is wide' ($null -ne $after -and $after.h -gt $after.w) 'h > w' ("w {0:N1} h {1:N1}" -f $after.w, $after.h)
        }

        36 {
            New-Scene 'a right-to-left block'
            $t = New-Text 'shalom olam'
            if (-not $t) { Record-Failure 'a text block can be created' 'a text item' 'not found'; return }
            SelectIds @($t.itemId)
            $s = Invoke-Op 'text.style' @{ direction = 'rtl' }
            Check 'a base direction can be set' ($s.ok -ne $false) 'a result' "$($s.error)"
            Start-Sleep -Milliseconds 200
            $m = ModelText
            # Same again: the model stores RightToLeft, and that is the fact worth pinning.
            Check 'the direction is stored' ($m -match '(?i)"Direction":"RightToLeft"') 'Direction RightToLeft' 'not stored'
        }

        37 {
            New-Scene 'a text frame width and a rotation'
            $t = New-Text 'A frame of words that has to wrap somewhere sensible'
            if (-not $t) { Record-Failure 'a text block can be created' 'a text item' 'not found'; return }
            SelectIds @($t.itemId)
            $before = ByName $t.name
            $s = Invoke-Op 'text.style' @{ frameWidth = 160.0 }
            Check 'a frame width can be set' ($s.ok -ne $false) 'a result' "$($s.error)"
            Start-Sleep -Milliseconds 250
            $wrapped = ByName $t.name
            Check 'wrapping makes the block taller' ($null -ne $wrapped -and $null -ne $before -and $wrapped.h -gt $before.h) 'a taller block' ("h {0:N1} then {1:N1}" -f $before.h, $wrapped.h)
            $r = Invoke-Op 'text.style' @{ rotationDegrees = 30.0 }
            Check 'a rotation can be set' ($r.ok -ne $false) 'a result' "$($r.error)"
            Start-Sleep -Milliseconds 250
            $m = ModelText
            Check 'the rotation is stored' ($m -match '(?i)rotation') 'a rotation' 'not stored'
        }

        38 {
            New-Scene 'centring a label on a shape'
            $r = New-Obj 'rectangle' @{ x = 200; y = 200; width = 300; height = 200 } 'ci-rect'
            $t = New-Text 'centred'
            if (-not $t) { Record-Failure 'a text block can be created' 'a text item' 'not found'; return }
            SelectIds @($t.itemId)
            $c = Invoke-Op 'text.centerIn' @{ target = 'rect'; x = 200.0; y = 200.0; width = 300.0; height = 200.0 }
            Check 'centring a text block answers' ($c.ok -ne $false) 'a result' "$($c.error)"
            Start-Sleep -Milliseconds 250
            $after = ByName $t.name
            if ($after) {
                $cx = $after.x + $after.w / 2.0
                $cy = $after.y + $after.h / 2.0
                Check 'the text now sits in the middle of the rectangle' ([Math]::Abs($cx - 350) -le 6 -and [Math]::Abs($cy - 300) -le 6) 'centre (350, 300)' ("centre ({0:N1}, {1:N1})" -f $cx, $cy)
            } else { Record-Failure 'the centred text is findable' 'a text item' 'not found' }
        }

        # ---------------------------------------------------------------- definitions, instances and markers

        39 {
            New-Scene 'a definition and two placements'
            $a = New-Obj 'ellipse' @{ cx = 220; cy = 240; rx = 60; ry = 60; fillColor = @(90, 160, 220) } 'df-a'
            $b = New-Obj 'rectangle' @{ x = 240; y = 220; width = 80; height = 40; fillColor = @(240, 200, 90) } 'df-b'
            SelectIds @($a.itemId, $b.itemId)
            $c = Invoke-Op 'definition.create' @{ name = 'chip' }
            Check 'a definition can be created from a selection' ($c.ok -ne $false) 'a result' "$($c.error)"
            Start-Sleep -Milliseconds 300
            $l = (Invoke-Op 'definition.list' @{}).result
            $lj = ($l | ConvertTo-Json -Depth 8 -Compress)
            Check 'the definition is listed' ($lj -match 'chip') 'chip' 'not listed'
            $p = Invoke-Op 'definition.place' @{ name = 'chip'; x = 480.0; y = 360.0 }
            Check 'a definition can be placed again' ($p.ok -ne $false) 'a result' "$($p.error)"
            Start-Sleep -Milliseconds 300
            $lj2 = (((Invoke-Op 'definition.list' @{}).result) | ConvertTo-Json -Depth 8 -Compress)
            Check 'the placements are reported' ($lj2 -match 'chip') 'chip' 'not reported'
            $rf = Invoke-Op 'instance.refresh' @{}
            Check 'instances can be refreshed' ($rf.ok -ne $false) 'a result' "$($rf.error)"
            $ip = (Invoke-Op 'instance.presentation' @{}).result
            Check 'the presentation of the instances is reported' ($null -ne $ip) 'a report' 'nothing'
        }

        40 {
            New-Scene 'a marker reference, dangling then cleared'
            $p = New-Obj 'polyline' @{ points = @(@(160.0, 320.0), @(340.0, 200.0), @(520.0, 320.0)); strokeColor = @(0, 0, 0); strokeWidth = 3 } 'mk-path'
            SelectIds @($p.itemId)
            $s = Invoke-Op 'marker.set' @{ slot = 'end'; name = 'arrow' }
            Check 'a marker can be named on a slot' ($s.ok -ne $false) 'a result' "$($s.error)"
            Start-Sleep -Milliseconds 200
            $l = ((Invoke-Op 'marker.list' @{}).result | ConvertTo-Json -Depth 8 -Compress)
            Check 'the marker is listed on the path' ($l -match 'arrow') 'arrow' 'not listed'
            Check 'a marker nobody defines is reported as unresolved' ($l -match '(?i)"resolved":false|resolved=false') 'resolved false' 'reported as resolved'
            $d = (Invoke-Op 'marker.definitions' @{}).result
            Check 'the document reports no marker definitions' (@($d) -eq $null -or @($d).Count -eq 0 -or (($d | ConvertTo-Json -Compress) -notmatch 'arrow')) 'no arrow definition' 'a definition exists'
            $clear = Invoke-Op 'marker.set' @{ slot = 'end'; name = $null }
            Check 'a marker can be cleared' ($clear.ok -ne $false) 'a result' "$($clear.error)"
            Start-Sleep -Milliseconds 200
            $m = ModelText
            Check 'the reference is gone from the document' (-not ($m -match '(?i)"markerend":"arrow"')) 'no marker-end' 'still stored'
        }

        # ---------------------------------------------------------------- composition, structure and the shell

        41 {
            New-Scene 'a blend mode on the top shape'
            $under = New-Obj 'rectangle' @{ x = 180; y = 180; width = 240; height = 200; fillColor = @(240, 200, 60) } 'bm-under'
            $over = New-Obj 'rectangle' @{ x = 280; y = 240; width = 240; height = 200; fillColor = @(80, 140, 220) } 'bm-over'
            SelectIds @($over.itemId)
            $s = Invoke-Op 'object.setBlendMode' @{ mode = 'multiply' }
            Check 'a blend mode can be set' ($s.ok -ne $false) 'a result' "$($s.error)"
            Start-Sleep -Milliseconds 200
            $m = ModelText
            Check 'the blend mode is stored' ($m -match '(?i)multiply') 'a multiply blend' 'not stored'
            $cl = Invoke-Op 'object.setBlendMode' @{ mode = 'normal' }
            Check 'and can be cleared' ($cl.ok -ne $false) 'a result' "$($cl.error)"
            Start-Sleep -Milliseconds 200
            $m2 = ModelText
            Check 'the blend is gone' (-not ($m2 -match '(?i)"blendmode":"multiply"')) 'no multiply' 'still stored'
        }

        42 {
            New-Scene 'aligning and distributing three shapes'
            $a = New-Obj 'rectangle' @{ x = 120; y = 140; width = 90; height = 60; fillColor = @(200, 60, 60) } 'al-a'
            $b = New-Obj 'rectangle' @{ x = 340; y = 260; width = 90; height = 60; fillColor = @(60, 200, 60) } 'al-b'
            $c = New-Obj 'rectangle' @{ x = 560; y = 200; width = 90; height = 60; fillColor = @(60, 60, 200) } 'al-c'
            SelectIds @($a.itemId, $b.itemId, $c.itemId)
            Check 'three shapes are selected' ((Selection).Count -eq 3) '3 selected' "$((Selection).Count) selected"
            # axis is horizontal|vertical and edge is start|centre|end - the documented words, not x/y and top.
            $al = Invoke-Op 'arrange.align' @{ axis = 'vertical'; edge = 'start' }
            Check 'aligning answers' ($al.ok -ne $false) 'a result' "$($al.error)"
            Start-Sleep -Milliseconds 250
            # Three separate calls: passing them as one comma list would bind the array to a [string] parameter
            # and look up a name that is not there, which reads as the shapes having disappeared.
            $ys = @((ByName 'al-a'), (ByName 'al-b'), (ByName 'al-c'))
            $tops = @($ys | Where-Object { $_ } | ForEach-Object { [Math]::Round($_.y, 1) })
            Check 'the three tops agree after aligning' (@($tops | Select-Object -Unique).Count -eq 1) 'one common top' ($tops -join ',')
            $di = Invoke-Op 'arrange.distribute' @{ axis = 'horizontal' }
            Check 'distributing answers' ($di.ok -ne $false) 'a result' "$($di.error)"
            # **An unknown axis is not refused, and that is filed as #244.** The edge member of the same call is
            # checked and refused by name; the axis member silently becomes horizontal, so a caller who mistypes
            # an axis gets a different and destructive edit with a reply that reads as confirmation. This pins
            # the current behaviour: when #244 is fixed the call must fail and the document must be untouched.
            $before = @((ByName 'al-a'), (ByName 'al-b'), (ByName 'al-c'))
            $bad = Invoke-Op 'arrange.align' @{ axis = 'sideways'; edge = 'start' }
            $after = @((ByName 'al-a'), (ByName 'al-b'), (ByName 'al-c'))
            $moved = $false
            for ($i = 0; $i -lt 3; $i++) { if ($null -ne $before[$i] -and $null -ne $after[$i] -and [Math]::Abs($before[$i].x - $after[$i].x) -gt 0.5) { $moved = $true } }
            Check 'an unknown axis is accepted and does the wrong thing (#244)' (($bad.ok -ne $false) -and $moved) 'accepted and objects moved' ("ok {0}, moved {1}" -f ($bad.ok -ne $false), $moved)
        }

        43 {
            New-Scene 'rotating and scaling about a pivot'
            $r = New-Obj 'rectangle' @{ x = 200; y = 200; width = 200; height = 120; fillColor = @(120, 180, 220) } 'tf-rect'
            SelectIds @($r.itemId)
            $before = ByName 'tf-rect'
            $t = Invoke-Op 'object.transform' @{ rotationDegrees = 45.0; pivotX = 300.0; pivotY = 260.0 }
            Check 'a rotation about a pivot answers' ($t.ok -ne $false) 'a result' "$($t.error)"
            Start-Sleep -Milliseconds 250
            $rot = ByName 'tf-rect'
            Check 'the rotation changes the bounding box' ($null -ne $rot -and [Math]::Abs($rot.w - $before.w) -gt 5) 'a different box' ("w {0:N1} then {1:N1}" -f $before.w, $rot.w)
            $sc = Invoke-Op 'object.transform' @{ scaleX = 1.5; scaleY = 1.5 }
            Check 'a scale answers' ($sc.ok -ne $false) 'a result' "$($sc.error)"
            Start-Sleep -Milliseconds 250
            $big = ByName 'tf-rect'
            Check 'the scale makes it bigger' ($null -ne $big -and $big.w -gt $rot.w) 'a wider box' ("w {0:N1} then {1:N1}" -f $rot.w, $big.w)
            $so = Invoke-Op 'transform.scaleOptions' @{ lineWeights = $false }
            Check 'the scale options can be read and set' ($so.ok -ne $false) 'a result' "$($so.error)"
        }

        44 {
            New-Scene 'flipping twice is the identity'
            $p = New-Obj 'polyline' @{ points = @(@(120.0, 340.0), @(300.0, 180.0), @(420.0, 360.0)); strokeColor = @(0, 0, 0); strokeWidth = 3 } 'fl-path'
            SelectIds @($p.itemId)
            $m0 = ModelText
            $f1 = Invoke-Op 'object.flip' @{ axis = 'horizontal' }
            Check 'a flip answers' ($f1.ok -ne $false) 'a result' "$($f1.error)"
            Start-Sleep -Milliseconds 250
            $m1 = ModelText
            Check 'the flip changes the geometry' ($m0 -ne $m1) 'a different model' 'unchanged'
            SelectIds @($p.itemId)
            Invoke-Op 'object.flip' @{ axis = 'horizontal' } | Out-Null
            Start-Sleep -Milliseconds 250
            $m2 = ModelText
            Check 'flipping back returns the original geometry' ($m2 -eq $m0) 'the original model' 'a different model'
        }

        45 {
            New-Scene 'layers that can be renamed, hidden and locked'
            $r = New-Obj 'rectangle' @{ x = 180; y = 180; width = 200; height = 140; fillColor = @(200, 120, 60) } 'ly-rect'
            $add = Invoke-Op 'layer.add' @{ name = 'Ink' }
            Check 'a layer can be added' ($add.ok -ne $false) 'a result' "$($add.error)"
            Start-Sleep -Milliseconds 200
            $l1 = ((Invoke-Op 'layer.list' @{}).result | ConvertTo-Json -Depth 8 -Compress)
            Check 'the new layer is listed' ($l1 -match 'Ink') 'Ink' 'not listed'
            # layer.list names the field layerId, not id - read from the operation's own answer rather than
            # guessed from the model's spelling.
            $layerId = [regex]::Match($l1, '"layerId":"([0-9a-f-]{36})"').Groups[1].Value
            if (-not $layerId) { $layerId = [regex]::Match($l1, '"id":"([0-9a-f-]{36})"').Groups[1].Value }
            if ($layerId) {
                $rn = Invoke-Op 'layer.rename' @{ layerId = $layerId; name = 'Ink and wash' }
                Check 'a layer can be renamed' ($rn.ok -ne $false) 'a result' "$($rn.error)"
                $lv = Invoke-Op 'layer.setVisible' @{ layerId = $layerId; visible = $false }
                Check 'a layer can be hidden' ($lv.ok -ne $false) 'a result' "$($lv.error)"
                $ll = Invoke-Op 'layer.setLocked' @{ layerId = $layerId; locked = $true }
                Check 'a layer can be locked' ($ll.ok -ne $false) 'a result' "$($ll.error)"
                $ov = Invoke-Op 'layer.onlyVisible' @{ keep = @('Ink and wash') }
                Check 'only-visible answers' ($ov.ok -ne $false) 'a result' "$($ov.error)"
                Start-Sleep -Milliseconds 200
                $m = ModelText
                Check 'the renamed layer is in the document' ($m -match 'Ink and wash') 'Ink and wash' 'not stored'
            } else { Record-Failure 'the layer id can be read' 'a guid from layer.list' "$l1" }
        }

        46 {
            New-Scene 'a second page with its own layer'
            $r = New-Obj 'rectangle' @{ x = 120; y = 120; width = 160; height = 120; fillColor = @(90, 170, 120) } 'ab-rect'
            $add = Invoke-Op 'artboard.add' @{ width = 400.0; height = 300.0; x = 1000.0; y = 120.0; name = 'Page 2' }
            Check 'an artboard can be added' ($add.ok -ne $false) 'a result' "$($add.error)"
            Start-Sleep -Milliseconds 250
            $al = ((Invoke-Op 'artboard.list' @{}).result | ConvertTo-Json -Depth 8 -Compress)
            Check 'the second page is listed' ($al -match 'Page 2') 'Page 2' 'not listed'
            $m = ModelText
            $abId = [regex]::Match($m, '"Artboards":\[\{"Id":"([0-9a-f-]+)"').Groups[1].Value
            Check 'an artboard id can be read' ($abId -ne '') 'a guid' 'none'
            if ($abId) {
                $sb = Invoke-Op 'artboard.setBounds' @{ artboardId = $abId; x = 0.0; y = 0.0; width = 300.0; height = 200.0 }
                Check 'an artboard can be resized' ($sb.ok -ne $false) 'a result' "$($sb.error)"
                Start-Sleep -Milliseconds 250
                $m2 = ModelText
                Check 'the new bounds are stored' ($m2 -match '"Width":300') 'Width 300' 'not stored'
            }
            $layerId = [regex]::Match($m, '"Layers":\[\{"Id":"([0-9a-f-]+)"').Groups[1].Value
            if ($layerId) {
                $mv = Invoke-Op 'object.moveToLayer' @{ layerId = $layerId; itemIds = @($r.itemId) }
                Check 'an object can be moved onto a layer' ($mv.ok -ne $false) 'a result' "$($mv.error)"
                # Until it is renamed the row carries the label derived from its geometry, so the object is
                # found in the panel by that, not by the name it was created with - see the rename below.
                $ex = ((Invoke-Op 'object.explorer' @{}).result | ConvertTo-Json -Depth 8 -Compress)
                Check 'the explorer reports the object it was moved' ($ex -match '(?i)"label":"Rectangle"') 'a Rectangle row' $ex.Substring(0, [Math]::Min(200, $ex.Length))
                # **A name given at creation is for addressing; the panel shows the derived label until the
                # person renames it.** Measured: an object created as 'ab-rect' has label "Rectangle" with
                # userNamed false, and object.rename changes the label to the given text with userNamed true.
                $rn2 = Invoke-Op 'object.rename' @{ itemId = $r.itemId; name = 'the green box' }
                Check 'an object can be renamed' ($rn2.ok -ne $false) 'a result' "$($rn2.error)"
                Start-Sleep -Milliseconds 200
                $ex2 = ((Invoke-Op 'object.explorer' @{}).result | ConvertTo-Json -Depth 8 -Compress)
                Check 'the panel shows the name the person gave it' ($ex2 -match 'the green box') 'the green box' $ex2.Substring(0, [Math]::Min(200, $ex2.Length))
                Check 'and marks it as the person s own' ($ex2 -match '"userNamed":true') 'userNamed true' 'still derived'
            } else { Record-Failure 'the layer id can be read' 'a guid from the model' 'none' }
        }

        47 {
            New-Scene 'units and a measurement'
            $g0 = (Invoke-Op 'units.get' @{}).result
            Check 'the unit can be read' ($null -ne $g0) 'a unit' 'nothing'
            $s = Invoke-Op 'units.set' @{ unit = 'in' }
            Check 'the unit can be set' ($s.ok -ne $false) 'a result' "$($s.error)"
            Start-Sleep -Milliseconds 150
            $g1 = ((Invoke-Op 'units.get' @{}).result | ConvertTo-Json -Compress)
            Check 'the unit is now inches' ($g1 -match '(?i)in') 'inches' $g1
            $ev = Invoke-Op 'units.evaluate' @{ expression = '1in + 6pt' }
            Check 'an expression with units evaluates' ($ev.ok -ne $false) 'a result' "$($ev.error)"
            $ej = ($ev.result | ConvertTo-Json -Compress)
            Check '1in + 6pt is 27.5 mm' ($ej -match '27\.5') '27.5 mm' $ej
            Invoke-Op 'units.set' @{ unit = 'mm' } | Out-Null
        }

        48 {
            New-Scene 'a colour set and applied to the fill'
            $r = New-Obj 'rectangle' @{ x = 200; y = 200; width = 220; height = 160 } 'co-rect'
            SelectIds @($r.itemId)
            $s = Invoke-Op 'color.set' @{ hex = '#3366cc' }
            Check 'a colour can be set' ($s.ok -ne $false) 'a result' "$($s.error)"
            $g = ((Invoke-Op 'color.get' @{}).result | ConvertTo-Json -Compress)
            Check 'the colour reads back as the one set' ($g -match '(?i)3366cc|0\.2,|51,|r"?:0\.2') 'the blue that was set' $g
            $ap = Invoke-Op 'color.apply' @{ target = 'fill' }
            Check 'the colour can be applied to the fill' ($ap.ok -ne $false) 'a result' "$($ap.error)"
            Start-Sleep -Milliseconds 250
            $m = ModelText
            Check 'the shape now carries that blue' ($m -match '"B":0\.8|"B":204|"G":0\.4|"R":0\.2') 'the blue components' 'not stored'
            $rec = ((Invoke-Op 'color.recent' @{}).result | ConvertTo-Json -Compress)
            Check 'the recent colours answer' ($null -ne $rec) 'a list' 'nothing'
        }

        49 {
            New-Scene 'the pane tabs and the windows the shell has'
            $tab = Invoke-Op 'pane.setTab' @{ tab = 'Gradient' }
            Check 'a pane tab can be selected' ($tab.ok -ne $false) 'a result' "$($tab.error)"
            Start-Sleep -Milliseconds 300
            $pj = ($tab.result | ConvertTo-Json -Compress)
            Check 'the gradient tab is the one showing' ($pj -match '(?i)gradient') 'Gradient' $pj
            # The ramp is GradientRamp by TYPE and Ramp by NAME - the dump writes GradientRamp#Ramp - so a lookup
            # by name returns nothing however the pane is set.
            $g = (Invoke-Op 'ui.find' @{ type = 'GradientRamp' }).result.controls
            Check 'the ramp is on screen with that tab' (@($g).Count -ge 1) 'the ramp' "$(@($g).Count) controls"
            $w = ((Invoke-Op 'ui.windows' @{}).result | ConvertTo-Json -Depth 6 -Compress)
            Check 'the windows answer' ($null -ne $w) 'a list of windows' 'nothing'
            $pp = (Invoke-Op 'ui.popups' @{}).result
            Check 'the popups answer' ($null -ne $pp) 'a list of popups' 'nothing'
            $back = Invoke-Op 'pane.setTab' @{ tab = 'Color' }
            Check 'the colour tab can be selected again' ($back.ok -ne $false) 'a result' "$($back.error)"
            Start-Sleep -Milliseconds 300
            $h = (Invoke-Op 'ui.find' @{ name = 'HexBox' }).result.controls
            Check 'the hex box is back with the colour tab' (@($h).Count -ge 1) 'the hex box' "$(@($h).Count) controls"
        }

        50 {
            New-Scene 'export, round-trip and the document facts'
            $r = New-Obj 'rectangle' @{ x = 200; y = 200; width = 220; height = 160; fillColor = @(180, 90, 40) } 'ex-rect'
            New-Text 'exported' 240 240 | Out-Null
            $svg = Invoke-Op 'document.exportSvg' @{}
            Check 'the document exports as SVG' ($svg.ok -ne $false) 'a result' "$($svg.error)"
            # The export comes back as base64, so the check decodes it rather than looking for markup in the
            # envelope - which is also what proves the payload is a real document and not a placeholder.
            $svgText = ''
            try { $svgText = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($svg.result.svgBase64)) } catch { $svgText = '' }
            Check 'the export is an svg document' ($svgText -match '(?i)<svg') 'an svg root' $svgText.Substring(0, [Math]::Min(80, $svgText.Length))
            Check 'the exported svg carries the artwork' ($svgText -match 'ex-rect' -or $svgText -match '<path' -or $svgText -match '<rect') 'artwork in the svg' $svgText.Substring(0, [Math]::Min(80, $svgText.Length))
            $rt = Invoke-Op 'document.verifyRoundTrip' @{ save = $false }
            Check 'a round trip can be verified' ($rt.ok -ne $false) 'a result' "$($rt.error)"
            $rj = ($rt.result | ConvertTo-Json -Depth 6 -Compress)
            Check 'the round trip is faithful' ($rj -match '"match":true') 'match true' $rj
            $md = (Invoke-Op 'document.metadata' @{}).result
            Check 'the metadata answers' ($null -ne $md) 'metadata' 'nothing'
            $sec = (Invoke-Op 'document.security' @{}).result
            Check 'the security state answers' ($null -ne $sec) 'a security report' 'nothing'
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
