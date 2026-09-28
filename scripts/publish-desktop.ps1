<#
.SYNOPSIS
    Build and publish the VCCad desktop application natively on Windows.

.DESCRIPTION
    Produces self-contained desktop bundles for the requested runtime
    identifiers. This is the Windows-native counterpart of
    scripts/publish-desktop.sh and needs no WSL or cross-compilation:

        artifacts/desktop/win-x64/VCCad.App.Desktop.exe
        artifacts/desktop/linux-x64/VCCad.App.Desktop

    Both are built by the .NET SDK on this machine; only the bundle you ask for
    is produced by default (win-x64), so a normal Windows build is quick.

    Trimming is deliberately not offered: the lossless document serializer uses
    reflection-based System.Text.Json, so trimming breaks document loading at
    runtime. Single-file is off by default because SkiaSharp and HarfBuzz ship
    native libraries that then have to self-extract on first launch.

.PARAMETER Tag
    Optional version to stamp onto the bundles (for example 0.2.0 or v0.2.0).
    Anything that is not a version is rejected rather than handed to MSBuild.

.PARAMETER Rids
    Runtime identifiers to publish. Defaults to win-x64. Use
    -Rids win-x64,linux-x64 to produce both.

.PARAMETER SingleFile
    Bundle each platform into a single self-extracting executable.

.PARAMETER NoVerify
    Skip the post-publish launcher checks (existence and PE/ELF header).

.PARAMETER Run
    Launch the published Windows bundle after a successful publish.

.EXAMPLE
    ./scripts/publish-desktop.ps1
    ./scripts/publish-desktop.ps1 -Rids win-x64,linux-x64 -Tag 0.2.0
    ./scripts/publish-desktop.ps1 -Run
#>
[CmdletBinding()]
param(
    [string]$Tag = '',
    [string[]]$Rids = @('win-x64'),
    [switch]$SingleFile,
    [switch]$NoVerify,
    [switch]$Run
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    $project = 'src/VCCad.App.Desktop'
    $outRoot = 'artifacts/desktop'

    $versionArgs = @()
    if ($Tag) {
        $version = $Tag -replace '^refs/tags/', '' -replace '^v', ''
        if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([-.+][0-9A-Za-z.+-]+)?$') {
            throw "publish-desktop: '$Tag' is not a version tag like 0.2.0 or v0.2.0"
        }
        $versionArgs = @("-p:Version=$version", "-p:InformationalVersion=$version")
    }

    $singleFileArgs = @()
    if ($SingleFile) {
        $singleFileArgs = @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
    }

    Write-Host "publish-desktop: project        = $project"
    Write-Host "publish-desktop: rids           = $($Rids -join ', ')"
    Write-Host "publish-desktop: self-contained = true, single-file = $([bool]$SingleFile), trimmed = false"
    if ($Tag) { Write-Host "publish-desktop: version        = $version (from '$Tag')" }

    foreach ($rid in $Rids) {
        $out = Join-Path $outRoot $rid
        Write-Host ''
        Write-Host "==> dotnet publish $project -c Release -r $rid --self-contained true -o $out"

        $args = @(
            'publish', $project,
            '-c', 'Release',
            '-r', $rid,
            '--self-contained', 'true',
            '-p:PublishTrimmed=false',
            '-o', $out
        ) + $singleFileArgs + $versionArgs

        & dotnet @args
        if ($LASTEXITCODE -ne 0) {
            throw "publish-desktop: dotnet publish failed for $rid (exit $LASTEXITCODE)"
        }
    }

    Write-Host ''
    Write-Host 'publish-desktop: artifacts'
    $failed = $false
    foreach ($rid in $Rids) {
        $out = Join-Path $outRoot $rid
        $launcher = Join-Path $out 'VCCad.App.Desktop.exe'
        if (-not (Test-Path $launcher)) {
            $launcher = Join-Path $out 'VCCad.App.Desktop'
        }

        if (-not (Test-Path $launcher)) {
            Write-Warning "  $rid : MISSING launcher (expected VCCad.App.Desktop[.exe] in $out)"
            $failed = $true
            continue
        }

        $launcherBytes = (Get-Item $launcher).Length
        $bundleBytes = (Get-ChildItem -Recurse -File $out | Measure-Object -Property Length -Sum).Sum
        Write-Host ("  {0,-10} {1}" -f $rid, $launcher)
        Write-Host ("  {0,-10} launcher {1:N0} bytes ({2:N1} MiB), bundle {3:N0} bytes ({4:N1} MiB)" -f
            '', $launcherBytes, ($launcherBytes / 1MB), $bundleBytes, ($bundleBytes / 1MB))

        if (-not $NoVerify) {
            # Read the first two bytes: 'MZ' (0x4D 0x5A) for a PE/Win32 apphost,
            # 0x7F 'E' 'L' 'F' for a Linux one. Cheaper and more portable than
            # shelling out to `file`, which is not present on stock Windows.
            $head = [System.IO.File]::ReadAllBytes($launcher)[0..3]
            $isPe = $head[0] -eq 0x4D -and $head[1] -eq 0x5A
            $isElf = $head[0] -eq 0x7F -and $head[1] -eq 0x45 -and $head[2] -eq 0x4C -and $head[3] -eq 0x46
            $ok = if ($rid -like 'win-*') { $isPe } else { $isElf }
            $kind = if ($isPe) { 'PE32' } elseif ($isElf) { 'ELF' } else { 'unknown' }
            Write-Host ("  {0,-10} {1}" -f '', $kind)
            if (-not $ok) {
                Write-Warning "  $rid : expected $(if ($rid -like 'win-*') { 'PE32' } else { 'ELF' }), got $kind"
                $failed = $true
            }
        }
    }

    if ($failed) {
        throw 'publish-desktop: FAILED — missing or unexpected launcher(s)'
    }

    Write-Host ''
    Write-Host 'publish-desktop: OK'

    if ($Run) {
        $exe = Join-Path $outRoot 'win-x64/VCCad.App.Desktop.exe'
        if (-not (Test-Path $exe)) {
            throw "publish-desktop: -Run needs the win-x64 bundle (not produced)"
        }
        Write-Host "publish-desktop: launching $exe"
        Start-Process -FilePath $exe
    }
}
finally {
    Pop-Location
}
