# Does a text object draw anything on the canvas?
#
# The question came out of #219 and its neighbours: a text object's colour cannot be changed by any route, and
# every attempt to sample its ink came back pure white. The trouble with those attempts was never the editor -
# it was the sampler. So this probe does it the way the scenes do: the harness measures the canvas origin for
# the document it is working in, every gesture goes through it, and the comparison refuses to run unless the
# text was actually selected and actually deleted.
#
# If the box differs, the text draws and the ink colour can be read. If it does not, the text is not painted,
# which is silent, severe, and worth an issue with the frames attached.
. (Join-Path $PSScriptRoot 'drive.ps1')

Add-Type -AssemblyName System.Drawing

$script:ShotPath = @{}

function Shot {
    param([string]$name)
    $j = Invoke-RestMethod -Uri "http://127.0.0.1:5099/api/v1/screenshot" -TimeoutSec 30
    $path = Join-Path (Get-Location) ("artifacts\auto\$name.png")
    [System.IO.File]::WriteAllBytes($path, [Convert]::FromBase64String($j.result.pngBase64))
    return $path
}

# The screenshot is 1600x1000 for a 1920x1200 window; window coordinates have to be scaled onto it.
function ImageScale {
    param([string]$path)
    $bmp = [System.Drawing.Bitmap]::FromFile($path)
    $s = $bmp.Width / 1920.0
    $bmp.Dispose()
    return $s
}

Start-App -name 'text-ink' | Out-Null
New-Scene 'does text draw' | Out-Null
Calibrate | Out-Null
Write-Host ("origin for this document: ({0:N1},{1:N1})" -f $script:Ox, $script:Oy)

# A large, unmistakable target.
Tool 'ToolText' 'text' | Out-Null
ClickModel 150 150 | Out-Null
TypeText 'MMMM'
SetField 'TtSize' '48' 'text size' -NoUnit
Keys 'Escape'
Start-Sleep -Milliseconds 500

$item = LastItem
Write-Host ("text: '{0}' at ({1:N1},{2:N1}) {3:N1}x{4:N1}  id {5}" -f $item.text, $item.x, $item.y, $item.w, $item.h, $item.id)

$before = Shot 'ink-before'
$scale = ImageScale $before
$tl = (Invoke-Op 'view.toScreen' @{ x = $item.x; y = $item.y }).result
$br = (Invoke-Op 'view.toScreen' @{ x = ($item.x + $item.w); y = ($item.y + $item.h) }).result
$bx0 = [int]($tl.x * $scale); $by0 = [int]($tl.y * $scale)
$bx1 = [int]($br.x * $scale); $by1 = [int]($br.y * $scale)
Write-Host ("image box ({0},{1})..({2},{3}) at scale {4:N4}" -f $bx0, $by0, $bx1, $by1, $scale)

# Select it by its own bounds, through the harness's measured origin, and refuse to continue if the selection
# is not the text - otherwise the two frames both contain it and the comparison proves nothing.
# Selecting by clicking is exactly what fails when the object is not painted, so the frame test must not depend
# on it: click empty canvas with the selection tool to focus, select all by keyboard, and verify the selection
# is the text by id before anything is compared.
# Select without the canvas at all - the pointer and keyboard routes are what fail when the object is not
# painted, so the route that takes the id is the only one that can be trusted here.
Tool 'ToolSelect' 'select' | Out-Null
$apiSelect = (Invoke-Op 'selection.selectAll' @{}).result
Start-Sleep -Milliseconds 300
Write-Host ("selection.selectAll -> selected {0}" -f ((Selection) -join ','))
if (@(Selection).Count -lt 1) {
    $byId = (Invoke-Op 'selection.set' @{ ids = @($item.id) }).result
    Start-Sleep -Milliseconds 300
    Write-Host ("selection.set by id -> selected {0}" -f ((Selection) -join ','))
}
$selected = @(Selection)
if ($selected.Count -ne 1 -or $selected[0] -ne $item.id.ToLowerInvariant()) {
    Write-Host ("the marquee selected '{0}' rather than the text; stopping so the comparison means something" -f ($selected -join ',')) -ForegroundColor Yellow
    Write-Host ("   selection click description: {0}" -f $script:LastClick)
    exit 1
}

Invoke-Op 'object.delete' @{ id = $item.id } | Out-Null
Start-Sleep -Milliseconds 800
$remaining = ItemCount
if ($remaining -ne 0) {
    Write-Host ("the delete left {0} objects; stopping" -f $remaining) -ForegroundColor Yellow
    exit 1
}
$after = Shot 'ink-after'

$ia = [System.Drawing.Bitmap]::FromFile($before)
$ib = [System.Drawing.Bitmap]::FromFile($after)
$changed = 0; $sampled = 0
for ($px = $bx0; $px -le $bx1; $px++) {
    for ($py = $by0; $py -le $by1; $py++) {
        if ($px -lt 0 -or $py -lt 0 -or $px -ge $ia.Width -or $py -ge $ia.Height) { continue }
        $ca = $ia.GetPixel($px, $py); $cb = $ib.GetPixel($px, $py); $sampled++
        if ($ca.R -ne $cb.R -or $ca.G -ne $cb.G -or $ca.B -ne $cb.B) { $changed++ }
    }
}

$minLum = 999
for ($px = $bx0; $px -le $bx1; $px += 2) {
    for ($py = $by0; $py -le $by1; $py += 2) {
        if ($px -lt 0 -or $py -lt 0 -or $px -ge $ia.Width -or $py -ge $ia.Height) { continue }
        $c = $ia.GetPixel($px, $py)
        $lum = [int](0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B)
        if ($lum -lt $minLum) { $minLum = $lum }
    }
}

Write-Host ''
Write-Host ("inside the text's own box: {0} of {1} pixels changed when the text was deleted" -f $changed, $sampled)
Write-Host ("darkest pixel sampled in that box: luminance {0}" -f $minLum)
if ($changed -eq 0) {
    Write-Host 'VERDICT: the text is not painted - deleting it changed nothing where the model says it is.' -ForegroundColor Red
} else {
    Write-Host 'VERDICT: the text is painted; the frames above show its ink.' -ForegroundColor Green
}
Write-Host ("frames: {0} and {1}" -f $before, $after)
$ia.Dispose(); $ib.Dispose()
