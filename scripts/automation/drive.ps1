# Automation driver for the VCCad editor: gestures only for every document change, operations only to
# read state, set the viewport, take screenshots and record.
#
# Dot-sourced by the scene runner. Every check that fails is recorded with enough context to file it:
# what was done, what was expected, what the model actually held.

$ErrorActionPreference = 'Stop'
$script:Api = 'http://127.0.0.1:5099/api/v1/invoke'
$script:Ox = 0.0
$script:Oy = 0.0
$script:Scene = '(none)'
$script:Step = 0
$script:Failures = New-Object System.Collections.ArrayList
$script:Checks = 0
$script:Passes = 0
$script:Log = New-Object System.Collections.ArrayList

function Invoke-Op {
    param([string]$op, [hashtable]$params = @{})
    $body = @{ op = $op; params = $params } | ConvertTo-Json -Depth 14 -Compress
    try { return Invoke-RestMethod -Uri $script:Api -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 40 }
    catch { return $null }
}

function Wait-App {
    param([int]$seconds = 30)
    for ($i = 0; $i -lt ($seconds * 2); $i++) {
        $h = Invoke-Op 'app.ping' @{}
        if ($h) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Start-App {
    param([string]$name = 'auto-scenes')
    $exe = "src\VCCad.App.Desktop\bin\Release\net10.0\VCCad.App.Desktop.exe"
    $running = Get-Process -Name VCCad.App.Desktop -ErrorAction SilentlyContinue
    $launched = -not $running
    if ($launched) {
        Start-Process -FilePath $exe -ArgumentList '--size', '1920x1200', '--name', $name, '--port', '5099'
    }
    if (-not (Wait-App 40)) { throw 'the editor did not start' }
    if ($launched) {
        # A development build starts with the diagnostics overlay open and it covers the bottom of the
        # canvas. F12 is a *toggle*, so it is pressed only for a window this harness launched - pressing it
        # against an already-running window would open the overlay instead of closing it, and the harness
        # would be driving a different layout from the one it measured.
        Invoke-Op 'ui.keys' @{ keys = 'F12' } | Out-Null
    }
    Start-Sleep -Milliseconds 800
}

# ------------------------------------------------------------------ gestures

function Gesture {
    param($events, [int]$timeoutMs = 20000, [bool]$fast = $true)
    Invoke-Op 'input.batch' @{ events = $events; fast = $fast } | Out-Null
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 55
        $s = Invoke-Op 'input.status' @{}
        if ($s -and $s.result -and (-not $s.result.running)) {
            if ($s.result.error) {
                Record-Failure "gesture rejected" 'the batch to replay' $s.result.error
            }
            return $s.result
        }
    }
    Record-Failure "gesture timed out" 'the batch to finish' "$timeoutMs ms"
    return $null
}

function ClickAt {
    param([int]$x, [int]$y, [int]$count = 1)
    Invoke-Op 'input.pointer' @{ x = $x; y = $y; clickCount = $count } | Out-Null
    Start-Sleep -Milliseconds 160
}

# Both injection paths act on coordinates **relative to the canvas**; view.toScreen returns window pixels.
# So an aimed gesture subtracts the canvas origin, measured by Calibrate - for clicks and drags alike.
function ClickModel {
    param([double]$mx, [double]$my, [int]$count = 1)
    $r = Invoke-Op 'view.toScreen' @{ x = $mx; y = $my }
    if (-not $r -or -not $r.result) { return }
    # **A click and a drag must travel the same path.** input.pointer translates window coordinates to the
    # target control and input.batch does not, so ClickModel - which passes canvas-relative pixels - landed a
    # canvas-origin away from its target when it went through input.pointer: a caret click aimed at the middle
    # of a text arrived 76 pt off and the editor correctly started a new object, which I filed as a defect
    # twice. Both helpers build the same coordinates; this one now uses the operation the drag uses (#227).
    $x = [int]([double]$r.result.x - $script:Ox)
    $y = [int]([double]$r.result.y - $script:Oy)
    $events = @()
    for ($i = 0; $i -lt $count; $i++) {
        $events += @{ kind = 'down'; x = $x; y = $y; deltaMs = 40 }
        $events += @{ kind = 'up'; x = $x; y = $y; deltaMs = 40 }
    }
    Gesture $events 20000 $true | Out-Null
}
function DragModel {
    param([double]$x1, [double]$y1, [double]$x2, [double]$y2, [bool]$fast = $true)
    $a = Invoke-Op 'view.toScreen' @{ x = $x1; y = $y1 }
    $b = Invoke-Op 'view.toScreen' @{ x = $x2; y = $y2 }
    $ax = [int]([double]$a.result.x - $script:Ox); $ay = [int]([double]$a.result.y - $script:Oy)
    $bx = [int]([double]$b.result.x - $script:Ox); $by = [int]([double]$b.result.y - $script:Oy)
    $events = @()
    $events += @{ kind = 'down'; x = $ax; y = $ay; deltaMs = 40 }
    for ($i = 1; $i -le 8; $i++) {
        $events += @{ kind = 'move'; x = [int]($ax + (($bx - $ax) * $i / 8)); y = [int]($ay + (($by - $ay) * $i / 8)); deltaMs = 20 }
    }
    $events += @{ kind = 'up'; x = $bx; y = $by; deltaMs = 40 }
    Gesture $events 20000 $fast | Out-Null
}

function Hold {
    param([double]$mx, [double]$my, [int]$ms = 700)
    $p = Invoke-Op 'view.toScreen' @{ x = $mx; y = $my }
    $x = [int]([double]$p.result.x - $script:Ox); $y = [int]([double]$p.result.y - $script:Oy)
    $events = @(
        @{ kind = 'down'; x = $x; y = $y; deltaMs = 40 },
        @{ kind = 'up'; x = $x; y = $y; deltaMs = $ms }
    )
    Gesture $events ($ms + 15000) $false | Out-Null
}

# A hold at a WINDOW point, for controls: the canvas offset does not apply to chrome.
function HoldWindow {
    param([int]$x, [int]$y, [int]$ms = 700)
    $events = @(
        @{ kind = 'down'; x = $x; y = $y; deltaMs = 40 },
        @{ kind = 'up'; x = $x; y = $y; deltaMs = $ms }
    )
    Gesture $events ($ms + 15000) $false | Out-Null
}

function Keys {
    param([string]$keys)
    Invoke-Op 'ui.keys' @{ keys = $keys } | Out-Null
    Start-Sleep -Milliseconds 220
}

function TypeText {
    param([string]$text)
    Invoke-Op 'input.type' @{ text = $text; wpm = 0 } | Out-Null
    Start-Sleep -Milliseconds 320
}

function Find-Control {
    param([string]$name)
    $r = Invoke-Op 'ui.find' @{ name = $name }
    if (-not $r -or -not $r.result -or -not $r.result.controls) { return $null }
    return @($r.result.controls) | Select-Object -First 1
}

function ClickControl {
    param([string]$name)
    $c = Find-Control $name
    if (-not $c) { Record-Failure "control not found" $name 'ui.find returned nothing'; return $false }
    ClickAt ([int]($c.x + $c.width / 2)) ([int]($c.y + $c.height / 2))
    return $true
}

function Find-ByText {
    param([string]$text)
    $r = Invoke-Op 'ui.find' @{ text = $text }
    if (-not $r -or -not $r.result -or -not $r.result.controls) { return $null }
    return @($r.result.controls) | Select-Object -First 1
}

function ClickText {
    param([string]$text)
    $c = Find-ByText $text
    if (-not $c) { Record-Failure "control not found by text" $text 'ui.find returned nothing'; return $false }
    ClickAt ([int]($c.x + $c.width / 2)) ([int]($c.y + $c.height / 2))
    return $true
}

function Tool {
    param([string]$control, [string]$want)
    ClickControl $control | Out-Null
    $now = (Invoke-Op 'tool.get' @{}).result.tool
    if ($want -and $now -ne $want) { ClickControl $control | Out-Null; $now = (Invoke-Op 'tool.get' @{}).result.tool }
    if ($want -and $now -ne $want) { Record-Failure "tool did not activate" $want "tool.get = $now" }
    return $now
}

# ------------------------------------------------------------------ reading the model

function DocText { return ((Invoke-Op 'ui.dump' @{ scope = 'document' }).result.text) }

function Items {
    $out = New-Object System.Collections.ArrayList
    foreach ($line in ((DocText) -split "`r?`n")) {
        if ($line -match '^\s*(path|ellipse|text|group)\b' -and $line -match '\[x=(-?[\d.]+) y=(-?[\d.]+) w=(-?[\d.]+) h=(-?[\d.]+)\]') {
            $item = [pscustomobject]@{
                kind = ($line.Trim() -split ' ')[0]
                x = [double]$Matches[1]; y = [double]$Matches[2]
                w = [double]$Matches[3]; h = [double]$Matches[4]
                text = ''
                id = ''
                line = $line.Trim()
            }
            if ($line -match 'id=([0-9a-fA-F-]+)') { $item.id = $Matches[1].ToLowerInvariant() }
            if ($line -match 'text\s+"[^"]*"\s+"([^"]*)"') { $item.text = $Matches[1] }
            $out.Add($item) | Out-Null
        }
    }
    return $out.ToArray()
}

function ItemCount { return (Items).Count }

function LastItem {
    $all = Items
    if ($all.Count -eq 0) { return $null }
    return $all[$all.Count - 1]
}

function Selection {
    $s = (Invoke-Op 'document.summary' @{}).result
    if ($s -and $s.selected) { return @($s.selected | ForEach-Object { $_.ToLowerInvariant() }) }
    return @()
}

# What the application says it is doing - "Type to enter text" is how it announces text editing, and it is
# the difference between a keystroke being lost and a click never entering the mode.
function StatusLine {
    $d = (Invoke-Op 'ui.dump' @{ scope = 'window'; maxNodes = 4000 }).result.text
    foreach ($l in ($d -split "`r?`n")) {
        if ($l -match 'StatusText.*?"(.*)"\s*$') { return $Matches[1] }
        if ($l -match 'StatusText') { return $l.Trim() }
    }
    return ''
}

function TextOf {
    param([string]$id = '')
    foreach ($item in (Items)) {
        if ($item.kind -ne 'text') { continue }
        if ($id -and $item.id -ne $id) { continue }
        return $item.text
    }
    return $null
}

# ------------------------------------------------------------------ checks

function Record-Failure {
    param([string]$what, [string]$expected, [string]$actual)
    $script:Failures.Add([pscustomobject]@{
            scene = $script:Scene
            step = $script:Step
            what = $what
            expected = $expected
            actual = $actual
        }) | Out-Null
    Write-Host ("   FAIL  [{0}] {1}: expected {2}, got {3}" -f $script:Scene, $what, $expected, $actual) -ForegroundColor Red
}

function Check {
    param([string]$what, [bool]$ok, [string]$expected, [string]$actual)
    $script:Checks++
    if ($ok) { $script:Passes++; return $true }
    Record-Failure $what $expected $actual
    return $false
}

function Step {
    param([string]$title)
    $script:Step++
    Write-Host ("   - {0}" -f $title) -ForegroundColor DarkGray
}

function New-Scene {
    param([string]$title)
    $script:Scene = $title
    $script:Step = 0
    Invoke-Op 'document.new' @{} | Out-Null
    Start-Sleep -Milliseconds 500
    Invoke-Op 'view.zoom' @{ factor = 1.0 } | Out-Null
    Start-Sleep -Milliseconds 250
    Invoke-Op 'view.centerOn' @{ x = 420.0; y = 300.0 } | Out-Null
    Start-Sleep -Milliseconds 350
    ClickControl 'ToolSelect' | Out-Null
    # **Bring the colour tab back.** HexBox lives in the Color pane and is only there while that tab shows,
    # so a scene that visits Gradient or Stroke leaves every later FillHex reporting "fill box missing" -
    # a leak between scenes that looks exactly like an editor fault.
    Invoke-Op 'pane.set' @{ pane = 'colors'; visible = $true } | Out-Null
    Start-Sleep -Milliseconds 150
    $tool = (Invoke-Op 'tool.get' @{}).result.tool
    if ($tool -ne 'select') { Record-Failure 'the selection tool activates for a new scene' 'select' "$tool" }
    Invoke-Op 'selection.clear' @{} | Out-Null
    # A click on empty canvas with the *selection* tool focuses the canvas without drawing anything -
    # the keyboard shortcuts below need that focus, and a click with a shape tool would create a shape.
    ClickModel 830 575 | Out-Null
    $stray = ItemCount
    if ($stray -ne 0) { Record-Failure 'a new document starts empty' '0 items' "$stray items" }

    # **Measure this document's canvas origin, with the correction zeroed first.** The viewport moves between
    # documents - the same model point mapped to window y 456 in one run and 628 in another - so an origin
    # measured once at start-up is wrong for the rest of the run. Zeroing is the whole point: the earlier
    # attempt measured with the old origin applied, so its probe landed where the correction said it would
    # and the measurement came back as zero. No arithmetic on top of arithmetic.
    $savedOx = $script:Ox
    $savedOy = $script:Oy
    $script:Ox = 0.0
    $script:Oy = 0.0
    Tool 'ToolRectangle' 'rectangle' | Out-Null
    DragModel 700 500 760 560
    $probe = LastItem
    if ($probe) {
        $script:Ox = $probe.x - 700.0
        $script:Oy = $probe.y - 500.0
    } else {
        $script:Ox = $savedOx
        $script:Oy = $savedOy
    }
    Keys 'Ctrl+Z'
    ClickControl 'ToolSelect' | Out-Null
    Invoke-Op 'selection.clear' @{} | Out-Null
    $stray = ItemCount
    if ($stray -ne 0) { Record-Failure 'the origin probe is undone before the scene runs' '0 items' "$stray items" }
}

function Calibrate {
    param([double]$x = 120.0, [double]$y = 120.0)
    # **The canvas' window origin is where it is drawn, not something to infer by drawing.** This used to
    # drag a rectangle and take the difference between the requested and the created position, which measures
    # the rounding of a synthetic drag - it reported (-0.3,-0.7) while the canvas actually sits at (50,93.3).
    # Every ClickModel gesture aimed through that pair then landed a canvas-origin away from its target, which
    # is how a caret click inside a text arrived forty points below it (#211 closed as that measurement error).
    # ui.find answers the question directly.
    $canvas = (Invoke-Op 'ui.find' @{ type = 'CanvasWorkspace' }).result.controls | Select-Object -First 1
    if (-not $canvas) { throw 'calibration could not find the canvas' }
    $script:Ox = [double]$canvas.x
    $script:Oy = [double]$canvas.y
    $script:Ow = [double]$canvas.width
    $script:Oh = [double]$canvas.height
    Write-Host ("calibrated: canvas at window ({0:N1},{1:N1}) {2:N1}x{3:N1}" -f $script:Ox, $script:Oy, $script:Ow, $script:Oh)
}

# ------------------------------------------------------------------ drawing helpers (gestures only)

function DrawRect {
    param([double]$x, [double]$y, [double]$w, [double]$h, [bool]$check = $true)
    $before = ItemCount
    Tool 'ToolRectangle' 'rectangle' | Out-Null
    DragModel $x $y ($x + $w) ($y + $h)
    if ($check) {
        $after = ItemCount
        Check 'a dragged rectangle is created' ($after -gt $before) "$($before + 1) items" "$after items"
    }
    return (LastItem)
}

function DrawEllipse {
    param([double]$x, [double]$y, [double]$w, [double]$h)
    $before = ItemCount
    Tool 'ToolEllipse' 'ellipse' | Out-Null
    DragModel $x $y ($x + $w) ($y + $h)
    Check 'a dragged ellipse is created' ((ItemCount) -gt $before) "$($before + 1) items" "$(ItemCount) items"
    return (LastItem)
}

function DrawPolygon {
    param($points, [string]$label = 'a polygon')
    $before = ItemCount
    Tool 'ToolPen' 'pen' | Out-Null
    foreach ($p in $points) { ClickModel $p[0] $p[1] | Out-Null }
    ClickModel $points[0][0] $points[0][1] | Out-Null
    Check "$label is created by pen clicks" ((ItemCount) -gt $before) "$($before + 1) items" "$(ItemCount) items"
    return (LastItem)
}

function DrawFreehand {
    param([double]$x, [double]$y)
    $before = ItemCount
    Tool 'ToolPencil' 'pencil' | Out-Null
    $events = @()
    $p = Invoke-Op 'view.toScreen' @{ x = $x; y = $y }
    $sx = [int]([double]$p.result.x - $script:Ox); $sy = [int]([double]$p.result.y - $script:Oy)
    $events += @{ kind = 'down'; x = $sx; y = $sy; deltaMs = 30 }
    for ($i = 1; $i -le 14; $i++) {
        $events += @{ kind = 'move'; x = $sx + ($i * 6); y = $sy + [int](12 * [Math]::Sin($i / 2.0)); deltaMs = 18 }
    }
    $events += @{ kind = 'up'; x = $sx + 84; y = $sy; deltaMs = 30 }
    Gesture $events 20000 $true | Out-Null
    Check 'a pencil stroke is created' ((ItemCount) -gt $before) "$($before + 1) items" "$(ItemCount) items"
    return (LastItem)
}

function FillHex {
    param([string]$hex)
    $box = Find-Control 'HexBox'
    if (-not $box) { Record-Failure 'fill box missing' 'HexBox' 'ui.find returned nothing'; return }
    ClickAt ([int]($box.x + $box.width / 2)) ([int]($box.y + $box.height / 2))
    Keys 'Ctrl+A'
    TypeText $hex
    Keys 'Enter'
    $model = (Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 14 -Compress
    if ($model -notmatch '"Fill":\{"Visible":true') {
        Record-Failure 'typing a hex into the fill box fills the shape' 'a visible fill' 'the model still shows none'
    }
}

function SetField {
    param([string]$field, [string]$value, [string]$label = '', [switch]$NoUnit)
    $box = Find-Control $field
    if (-not $box) { Record-Failure 'field missing' $field 'ui.find returned nothing'; return }
    # The panel works in millimetres - its own readout says so - and a bare number is read as the current
    # unit. Measured: 150 -> 425.2 pt, 300 -> 850.4, 240 -> 680.3, 120 -> 340.2, all exactly 2.8346 per
    # unit. So a check that means points must say points, and the hint in the panel is what told us:
    # "Type a measurement or a sum, e.g. 5.5in * 5 / 2".
    $withUnit = $value
    # The transform pane reads millimetres, so a bare number there means the current unit and a check that
    # means points has to say so. Not every field takes a unit, though: the text size control rejects "48 pt"
    # and silently keeps its old value, which looks exactly like the defect in #216 - so -NoUnit exists for
    # the fields whose hint does not invite one.
    if ((-not $NoUnit) -and ($value -match '^-?[0-9]+(\.[0-9]+)?$')) { $withUnit = "$value pt" }
    ClickAt ([int]($box.x + $box.width / 2)) ([int]($box.y + $box.height / 2))
    Keys 'Ctrl+A'
    TypeText $withUnit
    Keys 'Enter'
    if ($label) { Write-Host ("     {0} <- {1}" -f $label, $withUnit) -ForegroundColor DarkGray }
}

# ------------------------------------------------------------------ selection checks

function AssertSelection {
    param([string]$id, [string]$what)
    $sel = @(Selection)
    $ok = ($sel.Count -eq 1) -and ($sel[0] -eq $id.ToLowerInvariant())
    Check $what $ok "selection = $($id.ToLowerInvariant())" ("selection = " + ($sel -join ','))
}

# Switching to the selection tool is not optional: a click on the canvas with a *shape* tool active draws a
# shape instead of selecting one, so every selection check must arm the tool first. Without this the scenes
# drew tiny rectangles where they meant to click, the selection stayed empty, and the item counts grew.
function ClearSelection {
    ClickControl 'ToolSelect' | Out-Null
    Invoke-Op 'selection.clear' @{} | Out-Null
    Start-Sleep -Milliseconds 120
}

function SelectByOutline {
    param($item)
    if (-not $item) { return }
    ClickControl 'ToolSelect' | Out-Null
    # A stroke is a one-pixel target and an injected click is rounded to whole pixels, so each edge is
    # tried with the neighbouring pixels too. A person can see the line they are aiming at; this cannot.
    # Only when every edge and every neighbour misses is "selection is broken" a fair conclusion.
    $want = $item.id.ToLowerInvariant()
    $points = @(
        @(($item.x + $item.w / 2), $item.y),
        @($item.x, ($item.y + $item.h / 2)),
        @(($item.x + $item.w), ($item.y + $item.h / 2)),
        @(($item.x + $item.w / 2), ($item.y + $item.h))
    )
    foreach ($p in $points) {
        foreach ($d in @(@(0, 0), @(0, 1), @(0, -1), @(1, 0), @(-1, 0))) {
            ClickModel 835 585 | Out-Null
            ClickModel ($p[0] + $d[0]) ($p[1] + $d[1]) | Out-Null
            $sel = Selection
            $sel = @(Selection)
            if ($sel.Count -eq 1 -and $sel[0] -eq $want) { return }
        }
    }
}
function SelectByInterior {
    param($item)
    if (-not $item) { return }
    ClickControl 'ToolSelect' | Out-Null
    ClickModel ($item.x + $item.w / 2) ($item.y + $item.h / 2) | Out-Null
}

# ------------------------------------------------------------------ report

function Write-Report {
    param([string]$path)
    $report = [pscustomobject]@{
        scenes = $script:Scene
        checks = $script:Checks
        passes = $script:Passes
        failures = $script:Failures.Count
        items = $script:Failures
    }
    $report | ConvertTo-Json -Depth 6 | Set-Content -Path $path -Encoding utf8
    Write-Host ''
    Write-Host ("checks {0}: {1} passed, {2} failed" -f $script:Checks, $script:Passes, $script:Failures.Count)
    # Group by symptom so the same defect is not reported once per scene.
    $script:Failures | Group-Object what | Sort-Object Count -Descending | ForEach-Object {
        Write-Host ("  x{0}  {1}" -f $_.Count, $_.Name)
    }
}
