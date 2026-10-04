# Isolate the harness's click path: harness functions only, no scenes, everything printed.
. (Join-Path $PSScriptRoot 'drive.ps1')

$script:OnlyProbe = $true
Start-App -name 'probe-clicks' | Out-Null
New-Scene 'click probe'
Calibrate | Out-Null
Write-Host ("origin ({0:N1},{1:N1})" -f $script:Ox, $script:Oy)

# Draw one rectangle through the harness's own drag helper.
$before = ItemCount
$item = DrawRect 200 200 160 120
Write-Host ("drawn: {0}" -f $item.line)
Write-Host ("dump id {0}" -f $item.id)

# Aim at its top edge with the harness's own click helper.
$targetX = $item.x + $item.w / 2
$targetY = $item.y
$p = Invoke-Op 'view.toScreen' @{ x = $targetX; y = $targetY }
$injectX = [int]([double]$p.result.x - $script:Ox)
$injectY = [int]([double]$p.result.y - $script:Oy)
Write-Host ("target model ({0:N1},{1:N1}) -> window ({2:N1},{3:N1}) -> inject ({4},{5})" -f $targetX, $targetY, $p.result.x, $p.result.y, $injectX, $injectY)

Tool 'ToolSelect' 'select' | Out-Null
Invoke-Op 'selection.clear' @{} | Out-Null
Start-Sleep -Milliseconds 250
Write-Host ("before the click, selected: '{0}'" -f (((Invoke-Op 'document.summary' @{}).result.selected) -join ','))

$r = Invoke-Op 'input.pointer' @{ x = $injectX; y = $injectY }
Write-Host ("click returned: {0}" -f $r.result.action)
Start-Sleep -Milliseconds 300
Write-Host ("after the click, selected: '{0}'" -f (((Invoke-Op 'document.summary' @{}).result.selected) -join ','))

# And through the helper itself, to be sure the two agree.
Invoke-Op 'selection.clear' @{} | Out-Null
Start-Sleep -Milliseconds 250
ClickModel $targetX $targetY | Out-Null
Write-Host ("after ClickModel, selected: '{0}'" -f (((Invoke-Op 'document.summary' @{}).result.selected) -join ','))
Write-Host ("items now: {0}  (was {1} before the rectangle)" -f (ItemCount), $before)
