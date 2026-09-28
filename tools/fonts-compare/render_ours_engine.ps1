# Render every page with OUR engine (the running app) at 72 dpi, one image per page.
$ErrorActionPreference = 'Stop'
$out = 'C:\Users\submu\vccad-win\artifacts\eng'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $out -Force | Out-Null

function Op($op, $params) {
    $b = @{ op = $op; params = $params } | ConvertTo-Json -Depth 8
    (Invoke-RestMethod -Uri 'http://127.0.0.1:5099/api/v1/invoke' -Method Post `
        -ContentType 'application/json' -Body $b -TimeoutSec 600)
}

$boards = @((Op 'artboard.list' @{}).result)
Write-Host "artboards: $($boards.Count)"

Op 'view.zoom' @{ factor = 1.0 } | Out-Null

for ($i = 0; $i -lt $boards.Count; $i++) {
    $b = $boards[$i]
    $cx = $b.x + $b.width / 2.0
    $cy = $b.y + $b.height / 2.0
    Op 'view.centerOn' @{ x = $cx; y = $cy } | Out-Null
    Start-Sleep -Milliseconds 400

    $shot = Invoke-RestMethod -Uri 'http://127.0.0.1:5099/api/v1/screenshot' -TimeoutSec 120
    $path = Join-Path $out ("eng-{0:D2}.png" -f ($i + 1))
    [System.IO.File]::WriteAllBytes($path, [Convert]::FromBase64String($shot.result.pngBase64))
    Write-Host ("page {0,2}: centred on ({1:F0},{2:F0})  {3}x{4}" -f ($i + 1), $cx, $cy, $b.width, $b.height)
}

Write-Host "rendered $($boards.Count) pages with our engine"
