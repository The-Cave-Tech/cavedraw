<#
.SYNOPSIS
    Render the same page with VCCad and with Inkscape and report how far apart they are.

.DESCRIPTION
    Issue #131. This is the driving half of the Inkscape comparison harness; the test half is
    tests/VCCad.App.Tests/InkscapeComparisonTests.cs and the measurement itself is
    tools/inkscape-compare/compare.py.

    It takes a VCCad PNG and an SVG and prints the size, the mean absolute channel difference, the
    largest channel difference and the share of pixels that differ by more than a tolerance. A
    difference is data, not an error, so the script exits 0 either way — the number is what you came
    for.

    Two ways to get the two inputs:

      -FromApp   talk to a running VCCad instance (--port 5099 by default) and ask it for both:
                 document.renderPage writes the PNG and document.saveSvgToFile writes the SVG.
                 This is the honest path: one document, two renderers.

      -Svg and -VccadPng   use files you already have, from a test run's VCCAD_RENDER_ARTIFACTS
                 directory or from anywhere else.

    Windows-native. Inkscape is found at its usual install location, or via VCCAD_INKSCAPE.
    compare.py needs Pillow and numpy, which are already a prerequisite of this repository.

.EXAMPLE
    ./scripts/compare-inkscape.ps1 -FromApp
    ./scripts/compare-inkscape.ps1 -Svg artifacts/inkscape-compare/page.svg -VccadPng artifacts/inkscape-compare/vccad.png
    ./scripts/compare-inkscape.ps1 -FromApp -Dpi 144 -Tolerance 4
#>
[CmdletBinding(DefaultParameterSetName = 'FromApp')]
param(
    [Parameter(ParameterSetName = 'FromApp', Mandatory = $true)]
    [switch]$FromApp,

    [Parameter(ParameterSetName = 'Files', Mandatory = $true)]
    [string]$Svg,

    [Parameter(ParameterSetName = 'Files', Mandatory = $true)]
    [string]$VccadPng,

    [string]$BaseUrl = 'http://127.0.0.1:5099',

    [int]$Page = 0,

    [double]$Dpi = 72,

    [int]$Tolerance = 8,

    [int]$Inset = 0,

    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $repo 'artifacts/inkscape-compare' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) { $python = Get-Command python3 -ErrorAction SilentlyContinue }
if (-not $python) { throw 'no python on PATH: compare.py needs Pillow and numpy' }
$compare = Join-Path $repo 'tools/inkscape-compare/compare.py'

if ($PSCmdlet.ParameterSetName -eq 'FromApp') {
    $svgPath = Join-Path $OutDir 'page.svg'
    $vccadPath = Join-Path $OutDir 'vccad.png'

    $invoke = { param($op, $params)
        $body = @{ op = $op; params = $params } | ConvertTo-Json -Depth 6
        Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/invoke" -ContentType 'application/json' -Body $body
    }

    Write-Host "asking $BaseUrl for page $Page"

    # The page render goes through the same canvas the person sees, so it carries the editor's
    # chrome: a selected object brings its dashed bounding box and eight handles with it, and those
    # pixels are not in the SVG Inkscape rasterises. Measured on this machine over a filled
    # rectangle and a 2 pt line, leaving the selection in added 1,288 differing pixels (0.26% of
    # the page, seven of them at a full 255) on top of the 567 that anti-aliasing accounts for.
    # Clear it first, or the comparison measures the editor rather than the document.
    & $invoke 'selection.clear' @{} | Out-Null

    & $invoke 'document.renderPage' @{ page = $Page; dpi = $Dpi; path = $vccadPath } | Out-Null
    & $invoke 'document.saveSvgToFile' @{ path = $svgPath; page = $Page } | Out-Null
    $Svg = $svgPath
    $VccadPng = $vccadPath
}

$inkscapePng = Join-Path $OutDir 'inkscape.png'
& $python.Source $compare render $Svg -o $inkscapePng --dpi $Dpi

Write-Host ''
Write-Host "VCCad  : $VccadPng"
Write-Host "Inkscape: $inkscapePng  (from $Svg)"
& $python.Source $compare compare $VccadPng $inkscapePng --tolerance $Tolerance --inset $Inset
if ($Inset -eq 0) {
    Write-Host ''
    Write-Host 'The whole page includes the artboard frame the editor draws and Inkscape does not.'
    Write-Host 'Re-run with -Inset 1 to measure the artwork rather than the chrome.'
}
