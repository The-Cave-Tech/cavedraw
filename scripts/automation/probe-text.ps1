# Does typing reach an existing text object? Creation-typing is the control; three ways of re-entering
# the text are the test. The witness is the stored string, not the status line.
. (Join-Path $PSScriptRoot 'drive.ps1')

Start-App -name 'text-probe' | Out-Null
New-Scene 'text re-entry' | Out-Null
Calibrate | Out-Null

function Show-Text {
    param([string]$label)
    $item = LastItem
    $text = '(none)'
    if ($item) { $text = $item.text }
    Write-Host ("   {0}: '{1}'   [{2}]" -f $label, $text, (StatusLine))
}

# --- the control: type during creation, which is the gesture that always worked -----------------
Tool 'ToolText' 'text' | Out-Null
ClickModel 150 150 | Out-Null
TypeText 'Alpha'
Show-Text 'after creating and typing'

# typing again immediately, still in the creation session
TypeText 'Beta'
Show-Text 'after typing again straight away'

$item = LastItem
Write-Host ("   text object at ({0:N1},{1:N1}) {2:N1}x{3:N1}  id {4}" -f $item.x, $item.y, $item.w, $item.h, $item.id)

# --- A: arm the text tool, click inside the words, type ----------------------------------------
Tool 'ToolText' 'text' | Out-Null
ClickModel ($item.x + $item.w / 2) ($item.y + $item.h / 2) | Out-Null
TypeText 'B'
Show-Text 'A: text tool, click inside, type B'

# --- B: selection tool, double-click the first word, type over it ------------------------------
Tool 'ToolSelect' 'select' | Out-Null
ClickModel ($item.x + 8) ($item.y + $item.h / 2) 2 | Out-Null
TypeText 'C'
Show-Text 'B: double-click a word, type C'

# --- C: selection tool, click the outline to select, Enter, type ------------------------------
Tool 'ToolSelect' 'select' | Out-Null
ClickModel ($item.x + $item.w / 2) $item.y | Out-Null
$sel = @(Selection)
Write-Host ("   C: selected {0}" -f ($sel -join ','))
Keys 'Enter'
Start-Sleep -Milliseconds 400
TypeText 'D'
Show-Text 'C: Enter then type D'

# --- D: does the keyboard reach the canvas at all in this state? -------------------------------
Tool 'ToolText' 'text' | Out-Null
ClickModel 300 300 | Out-Null
TypeText 'Second'
Show-Text 'D: a second text object typed fresh'
