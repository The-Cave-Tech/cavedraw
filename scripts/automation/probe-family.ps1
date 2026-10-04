# Which text gesture leaves a run with no font family?
#
# The crash behind #213 came from a text run whose family is null. This walks the text toolbar gestures one
# at a time and reports what the model holds after each, so the gesture that nulls it names itself.
. (Join-Path $PSScriptRoot 'drive.ps1')

Start-App -name 'family-probe' | Out-Null
New-Scene 'family probe' | Out-Null
Calibrate | Out-Null

function FamilyReport {
    $j = (Invoke-Op 'document.model' @{}).result | ConvertTo-Json -Depth 16 -Compress
    $nulls = ([regex]::Matches($j, '"[Ff]ontFamily"\s*:\s*null')).Count
    $values = @()
    foreach ($m in [regex]::Matches($j, '"[Ff]ontFamily"\s*:\s*"([^"]*)"')) { $values += $m.Groups[1].Value }
    $blanks = 0
    foreach ($v in $values) { if ([string]::IsNullOrWhiteSpace($v)) { $blanks++ } }
    return ("null={0} blank={1} values=[{2}]" -f $nulls, $blanks, (($values | Select-Object -Unique) -join ','))
}

function Nudge {
    param([string]$label)
    Write-Host ("   {0}: {1}" -f $label, (FamilyReport))
}

# A text object to work on.
Tool 'ToolText' 'text' | Out-Null
ClickModel 150 150 | Out-Null
TypeText 'Sample'
Nudge 'after creating text'

# The controls the text toolbar offers while the text tool is armed.
$controls = @('TtSize', 'TtLineSpacing', 'TtParagraphSpacing', 'TtRotation', 'TtColor')
foreach ($name in $controls) {
    $box = Find-Control $name
    if (-not $box) { Write-Host ("   {0}: control not found" -f $name); continue }
    ClickAt ([int]($box.x + $box.width / 2)) ([int]($box.y + $box.height / 2))
    Keys 'Ctrl+A'
    TypeText '18'
    Keys 'Enter'
    Nudge "after $name"
}

# The combo boxes: opening one and choosing the first entry is the gesture that picks a face.
foreach ($name in @('TtFont', 'TtAlign')) {
    $combo = Find-Control $name
    if (-not $combo) { Write-Host ("   {0}: control not found" -f $name); continue }
    ClickAt ([int]($combo.x + 12)) ([int]($combo.y + $combo.height / 2))
    Start-Sleep -Milliseconds 600
    $items = (Invoke-Op 'ui.list' @{ name = $name }).result
    $count = 0
    if ($items -and $items.items) { $count = @($items.items).Count }
    Write-Host ("   {0}: opened, {1} entries" -f $name, $count)
    Keys 'Escape'
    Nudge "after opening $name"
}

# The style keys inside the text.
Keys 'Ctrl+A'
Keys 'Ctrl+B'
Nudge 'after Ctrl+B'
Keys 'Ctrl+I'
Nudge 'after Ctrl+I'
Keys 'Escape'
Nudge 'after Escape'

# And what a saved-and-reloaded document holds, which is how a null would survive a session.
$path = 'artifacts\auto\family-probe.vccad.json'
Invoke-Op 'document.savePdfToFile' @{ path = (Join-Path (Get-Location) 'artifacts\auto\family-probe.pdf') } | Out-Null
Write-Host ("   saved a PDF for inspection: {0}" -f (Test-Path 'artifacts\auto\family-probe.pdf'))
