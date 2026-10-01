<#
.SYNOPSIS
    Build and test every VCCad project natively on Windows.

.DESCRIPTION
    `dotnet test VCCad.sln` cannot run on a Windows box that has no `wasm-tools`
    workload, because the solution contains the WebAssembly host
    (src/VCCad.App.Browser) and building it needs Emscripten. This script builds
    and tests the desktop-relevant projects instead, so a Windows developer can
    run the whole suite with one command.

    The web version is built and verified too, when the workload is installed: it
    is a shipped target - the Docker image serves the bundle - and it runs the same
    shell and the same operation registry, so a change that breaks the browser head
    should be found here rather than by CI after a push. Without the workload the
    web step skips cleanly, the way corpus tests skip without their corpus.

    Reference corpora are detected automatically and exported for the child test
    processes, which is what makes the PDF corpus sweeps expand from ~120 tests
    to ~3,500. Corpora are never modified.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER NoWeb
    Do not build or verify the web (WebAssembly) bundle. Useful for a quick loop on
    the desktop suites; CI builds the bundle regardless.

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
    ./scripts/test-all.ps1 -NoWeb
    ./scripts/test-all.ps1 -Filter "FullyQualifiedName~AiPrivateData"
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoCorpus,
    [switch]$NoWeb,
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

    # The web version, which is a shipped target: the Docker image serves this bundle and it runs the **same**
    # shell and the same operation registry. `dotnet build VCCad.sln` deliberately cannot run on a box without
    # wasm-tools, and this script used to avoid the whole solution - including the project that needs the
    # workload. Avoiding the *solution* is right; avoiding the *project* meant nothing local ever built the web
    # version, so a change that compiled for net10.0 and broke the browser head was found by CI after a push.
    #
    # A machine without the workload gets a clean skip, the way every corpus test here skips without its corpus.
    $workloads = (& dotnet workload list 2>&1 | Out-String)
    if ($NoWeb) {
        Write-Host '==> -NoWeb: skipping the web build'
    }
    elseif ($workloads -notmatch 'wasm-tools') {
        Write-Host '==> wasm-tools workload not installed; skipping the web build (CI still builds it)'
    }
    else {
        Write-Host "==> dotnet publish src/VCCad.App.Browser -c $Configuration -o artifacts/web"
        & dotnet publish 'src/VCCad.App.Browser/VCCad.App.Browser.csproj' -c $Configuration -o 'artifacts/web'
        if ($LASTEXITCODE -ne 0) {
            throw "web publish failed (exit $LASTEXITCODE)"
        }

        # Verify the bundle the browser actually loads. The editor is matched by **name and shape**, because both
        # of the traps AGENTS.md records are live here: the assembly is not at the publish root, and a name-only
        # match also matches `runtimeconfig.json` - a check that passed for a year while proving nothing. A
        # *prefix* match is wrong too, because `VCCad.App.` also matches the six-kilobyte `VCCad.App.Browser.`
        # host; counting the dotted segments is what tells the editor from the host, since the hash has no dots.
        $wwwroot = 'artifacts/web/wwwroot'
        $framework = Join-Path $wwwroot '_framework'
        $problems = @()

        if (-not (Test-Path (Join-Path $wwwroot 'index.html'))) {
            $problems += "no index.html in $wwwroot"
        }

        if (-not (Get-ChildItem $framework -Filter 'dotnet*.js' -ErrorAction SilentlyContinue)) {
            $problems += "no dotnet*.js in $framework"
        }

        $editor = Get-ChildItem $framework -ErrorAction SilentlyContinue | Where-Object {
            ($_.Name -like 'VCCad.App.*.wasm' -or $_.Name -like 'VCCad.App.*.webcil') -and
            $_.Name.Split('.').Count -eq 4
        } | Select-Object -First 1

        if (-not $editor) {
            $problems += "no VCCad.App.<hash>.wasm or .webcil in $framework - the bundle has no editor in it"
        }
        elseif ($editor.Length -lt 200000) {
            $problems += "$($editor.Name) is only $($editor.Length) bytes, which is not the editor"
        }

        if ($problems.Count -gt 0) {
            throw "web bundle check failed: $($problems -join '; ')"
        }

        Write-Host ("    web bundle OK: {0} ({1:N1} MB)" -f $editor.Name, ($editor.Length / 1MB))
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
        if (-not $cache) {
            Write-Host '==> no corpus cache found; corpus sweeps will skip (still green)'
        }

        if ($cache) {
            $gs = Join-Path $cache 'ghostscript'
            $ai = Join-Path $cache 'ai'
            if (Test-Path $gs) { $env:VCCAD_GS_CORPUS = $gs }
            if (Test-Path $ai) { $env:VCCAD_AI_CORPUS = $ai }
            Write-Host "==> corpora root: $cache"
            Write-Host "    VCCAD_GS_CORPUS=$($env:VCCAD_GS_CORPUS)"
            Write-Host "    VCCAD_AI_CORPUS=$($env:VCCAD_AI_CORPUS)"
        }

        # The copyrighted sample patterns are not in this repository: it is public and they cannot be
        # redistributed. They live in a private checkout beside it, or wherever VCCAD_SAMPLES points. Their
        # absence skips the sample tests rather than failing anything - the samples add coverage, they are not
        # a prerequisite for a green run.
        if (-not $env:VCCAD_SAMPLES) {
            $beside = Join-Path (Split-Path $PSScriptRoot -Parent) '..\samples'
            if (Test-Path $beside) { $env:VCCAD_SAMPLES = (Resolve-Path $beside).Path }
        }

        if ($env:VCCAD_SAMPLES) {
            Write-Host "    VCCAD_SAMPLES=$($env:VCCAD_SAMPLES)"
        }
        else {
            Write-Host "    VCCAD_SAMPLES=(not checked out - the sample pattern tests will skip)"
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

        # A filter that matches nothing in **this** project is not a failure. Without this, `-Filter
        # "FullyQualifiedName~AiPrivateData"` - the documented way to run one feature's tests - reported the four
        # projects with no matching test as failures and made the run unusable for the case it names.
        $noMatch = $output | Select-String -Pattern 'No test matches' -Quiet

        if ($summary) {
            Write-Host ("    {0}" -f ($summary.ToString().Trim()))
            if ($summary -match 'Passed:\s+(\d+)') { $total += [int]$Matches[1] }
            if ($summary -match 'Failed:\s+(\d+)' -and [int]$Matches[1] -gt 0) { $failed += $name }
        }
        elseif ($noMatch) {
            Write-Host '    no test matched the filter in this project (not a failure)'
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
