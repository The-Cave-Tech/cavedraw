<#
.SYNOPSIS
    Build and test every VCCad project natively on Windows.

.DESCRIPTION
    `dotnet test VCCad.sln` cannot run on a Windows box that has no `wasm-tools`
    workload, because the solution contains the WebAssembly host
    (src/VCCad.App.Browser) and building it needs Emscripten. This script builds
    and tests the desktop-relevant projects instead, so a Windows developer can
    run the whole suite with one command.

    Reference corpora are detected automatically and exported for the child test
    processes, which is what makes the PDF corpus sweeps expand from ~120 tests
    to ~3,500. Corpora are never modified.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER NoCorpus
    Do not export corpus paths, even when they are found. Useful for a fast run
    that exercises the skip-sentinel paths.

.PARAMETER CorpusRoot
    Optional explicit corpus cache root. Defaults to probing the environment
    variables first, then %USERPROFILE%\.cache\vccad-corpora, then the WSL cache
    mounted through \\wsl.localhost.

.PARAMETER Filter
    Optional xUnit filter, passed through to dotnet test.

.EXAMPLE
    ./scripts/test-all.ps1
    ./scripts/test-all.ps1 -NoCorpus
    ./scripts/test-all.ps1 -Filter "FullyQualifiedName~AiPrivateData"
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoCorpus,
    [string]$CorpusRoot = '',
    [string]$Filter = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    # The WebAssembly host needs the wasm-tools workload; everything else does not.
    $projects = @(
        'tests/VCCad.Geometry.Tests/VCCad.Geometry.Tests.csproj',
        'tests/VCCad.Core.Tests/VCCad.Core.Tests.csproj',
        'tests/VCCad.Pdf.Tests/VCCad.Pdf.Tests.csproj',
        'tests/VCCad.App.Tests/VCCad.App.Tests.csproj',
        'tests/VCCad.Api.Integration.Tests/VCCad.Api.Integration.Tests.csproj'
    )

    # Desktop host + libraries, built first so a compile error surfaces once
    # rather than five times.
    Write-Host "==> dotnet build src/VCCad.App.Desktop -c $Configuration"
    & dotnet build 'src/VCCad.App.Desktop/VCCad.App.Desktop.csproj' -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "build failed (exit $LASTEXITCODE)"
    }

    if (-not $NoCorpus) {
        $roots = @()
        if ($CorpusRoot) {
            $roots += $CorpusRoot
        }
        else {
            if ($env:VCCAD_CORPUS_CACHE) { $roots += $env:VCCAD_CORPUS_CACHE }
            $roots += (Join-Path $env:USERPROFILE '.cache/vccad-corpora')
            $roots += '\\wsl.localhost\Ubuntu\home\darren\.cache\vccad-corpora'
        }

        $cache = $roots | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($cache) {
            $gs = Join-Path $cache 'ghostscript'
            $ai = Join-Path $cache 'ai'
            if (Test-Path $gs) { $env:VCCAD_GS_CORPUS = $gs }
            if (Test-Path $ai) { $env:VCCAD_AI_CORPUS = $ai }
            Write-Host "==> corpora root: $cache"
            Write-Host "    VCCAD_GS_CORPUS=$($env:VCCAD_GS_CORPUS)"
            Write-Host "    VCCAD_AI_CORPUS=$($env:VCCAD_AI_CORPUS)"
        }
        else {
            Write-Host '==> no corpus cache found; corpus sweeps will skip (still green)'
        }

        # The standard PDF fonts (Helvetica, Times, Courier, Symbol, ZapfDingbats) come
        # from the URW Core 35 files rather than from anything bundled with VCCad, so
        # point the suite at whichever copy this machine has — the distro package,
        # Ghostscript's own folder, or the WSL one reachable from Windows. Export tests
        # that need a programme to embed skip cleanly when none is found.
        $urwCandidates = @(
            $env:VCCAD_URW_FONTS,
            '/usr/share/fonts/opentype/urw-base35',
            '\\wsl.localhost\Ubuntu\usr\share\fonts\opentype\urw-base35',
            '\\wsl.localhost\Ubuntu\usr\share\ghostscript'
        ) | Where-Object { $_ }

        $urw = $urwCandidates | Where-Object { Test-Path (Join-Path $_ 'NimbusSans-Regular.otf') } |
            Select-Object -First 1
        if (-not $urw) {
            # Ghostscript layouts keep the fonts without an extension.
            $urw = $urwCandidates | Where-Object {
                Test-Path (Join-Path $_ 'Resource/Font/NimbusSans-Regular')
            } | ForEach-Object { Join-Path $_ 'Resource/Font' } | Select-Object -First 1
        }

        if ($urw) {
            $env:VCCAD_URW_FONTS = $urw
            Write-Host "    VCCAD_URW_FONTS=$urw"
        }
        else {
            Write-Host '==> no URW base-35 fonts found; export tests needing one will skip'
        }

        # The veraPDF corpus is optional and much larger; use it when it is around.
        foreach ($candidate in @(
                $env:VCCAD_VERAPDF_CORPUS,
                '\\wsl.localhost\Ubuntu\tmp\opencode\veraPDF-corpus',
                (Join-Path $env:USERPROFILE 'development/veraPDF-corpus'))) {
            if ($candidate -and (Test-Path $candidate)) {
                $env:VCCAD_VERAPDF_CORPUS = $candidate
                Write-Host "    VCCAD_VERAPDF_CORPUS=$candidate"
                break
            }
        }
    }

    $failed = @()
    $total = 0
    foreach ($project in $projects) {
        $name = Split-Path $project -Leaf
        Write-Host ''
        Write-Host "==> dotnet test $project -c $Configuration"

        $args = @('test', $project, '-c', $Configuration)
        if ($Filter) { $args += @('--filter', $Filter) }

        # A failing test writes to stderr, and with $ErrorActionPreference = 'Stop' that
        # becomes a TERMINATING error - so the first project with a failure ended the run and
        # the four after it never executed. A test summary that hides four projects is worse
        # than no summary: it reads as though everything else passed. stderr is data here.
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $output = & dotnet @args 2>&1
        }
        finally {
            $ErrorActionPreference = $previous
        }
        $output | Where-Object { $_ -match 'error (CS|MSB)\d' } | ForEach-Object { Write-Host "    $_" }
        $summary = $output | Select-String -Pattern 'Passed!|Failed!' | Select-Object -Last 1
        if ($summary) {
            Write-Host ("    {0}" -f ($summary.ToString().Trim()))
            if ($summary -match 'Passed:\s+(\d+)') { $total += [int]$Matches[1] }
            if ($summary -match 'Failed:\s+(\d+)' -and [int]$Matches[1] -gt 0) { $failed += $name }
        }
        else {
            Write-Host "    no test summary produced"
            $failed += $name
        }
    }

    Write-Host ''
    Write-Host ("test-all: {0} tests passed across {1} projects" -f $total, $projects.Count)
    if ($failed.Count -gt 0) {
        throw "test-all: FAILED in $($failed -join ', ')"
    }

    Write-Host 'test-all: OK'
}
finally {
    Pop-Location
}
