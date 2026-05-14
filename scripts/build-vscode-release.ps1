# Build the openvscode-server distribution on Windows and stage it as an
# embedded resource for the .NET library.
#
# PowerShell counterpart of scripts/build-vscode-release.sh. Defaults target
# win32-x64 (win64) since that is the only Windows distribution we ship.

[CmdletBinding()]
param(
    [ValidateSet('win32', 'linux', 'darwin', 'alpine')]
    [string]$Platform = 'win32',
    [ValidateSet('x64', 'arm64', 'ia32')]
    [string]$Arch = 'x64',
    [switch]$SkipNpmInstall
)

$ErrorActionPreference = 'Stop'

$distroName = "vscode-reh-web-$Platform-$Arch"

Write-Host "==> Building $distroName"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir   = Resolve-Path (Join-Path $scriptDir '..')
$buildRoot = Resolve-Path (Join-Path $rootDir  '..')

Write-Host "    repo: $rootDir"

Push-Location $rootDir
try {
    # --- optional: trim extensions for a pure C# / BYO-LSP build -----------
    # Skips the language stacks, JS/TS tooling, GitHub/MS auth flows and Node
    # debugger VSIXs that a customer running their own language server doesn't
    # need. Cuts ~15-30 min of build time.
    if ($env:VSCODE_MINIMAL_BUILD -eq '1') {
        Write-Host '==> VSCODE_MINIMAL_BUILD=1 -> trimming extensions'
        & node dotnet/scripts/prepare-minimal-build.mjs
        if ($LASTEXITCODE -ne 0) {
            throw "prepare-minimal-build.mjs exited with code $LASTEXITCODE"
        }
    }

    # --- npm install -------------------------------------------------------
    $shouldInstall = -not $SkipNpmInstall -and -not (Test-Path (Join-Path $rootDir 'node_modules'))
    if ($env:SKIP_NPM_INSTALL -eq '1') {
        $shouldInstall = $false
    }

    if ($shouldInstall) {
        Write-Host '==> npm install (pass -SkipNpmInstall or set SKIP_NPM_INSTALL=1 to skip)'
        # Skip heavy downloads we don't need for a reh-web build.
        $env:ELECTRON_SKIP_BINARY_DOWNLOAD   = '1'
        $env:PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD = '1'
        $env:npm_config_arch                  = $Arch
        $env:VSCODE_ARCH                      = $Arch

        # npm install can be flaky on Windows hosted agents — retry a few times.
        $maxAttempts = 5
        for ($i = 1; $i -le $maxAttempts; $i++) {
            try {
                & npm install
                if ($LASTEXITCODE -ne 0) { throw "npm install exited with code $LASTEXITCODE" }
                break
            }
            catch {
                if ($i -eq $maxAttempts) {
                    throw "npm install failed after $maxAttempts attempts: $_"
                }
                Write-Host "npm install failed attempt $i, retrying in 2s..."
                Start-Sleep -Seconds 2
            }
        }
    }
    else {
        Write-Host '==> Skipping npm install'
    }

    # --- gulp build --------------------------------------------------------
    # Invoke gulp directly so we can cap V8's heap below the package.json default of 8192 MiB.
    # Microsoft-hosted CI agents (ubuntu-latest, windows-latest, macos-latest) only have ~7 GiB
    # of RAM, so an 8 GiB heap forces the OS to swap during the minification stage and triggers
    # "Free memory is lower than 5%" agent warnings. 6144 MiB leaves headroom for the rest of
    # the toolchain (esbuild, terser, native module compilation) without OOM-ing on small hosts.
    # Override with VSCODE_NODE_MAX_OLD_SPACE_MB on beefier machines.
    $nodeMaxOldSpaceMb = if ($env:VSCODE_NODE_MAX_OLD_SPACE_MB) { $env:VSCODE_NODE_MAX_OLD_SPACE_MB } else { '6144' }
    $gulpTask = "$distroName-min"
    Write-Host "==> gulp $gulpTask (--max-old-space-size=$nodeMaxOldSpaceMb)"
    & node "--max-old-space-size=$nodeMaxOldSpaceMb" ./node_modules/gulp/bin/gulp.js $gulpTask
    if ($LASTEXITCODE -ne 0) {
        throw "gulp $gulpTask failed with exit code $LASTEXITCODE"
    }

    # The gulp output lives one level above the repo root.
    $buildOutput = Join-Path $buildRoot $distroName
    if (-not (Test-Path $buildOutput)) {
        throw "Expected gulp output directory not found: $buildOutput"
    }

    # --- package as tar.gz -------------------------------------------------
    $embedDir = Join-Path $rootDir 'dotnet/OpenVSCodeServer.Kestrel/EmbeddedAssets'
    if (-not (Test-Path $embedDir)) {
        New-Item -ItemType Directory -Path $embedDir | Out-Null
    }

    # Remove other architectures' archives so we always embed exactly one
    # matching distribution.
    Get-ChildItem -Path $embedDir -File -Filter 'vscode-reh-web-*.tar.gz' |
        ForEach-Object {
            Write-Host "    removing $($_.Name)"
            Remove-Item $_.FullName -Force
        }

    $archive = Join-Path $embedDir "$distroName.tar.gz"
    Write-Host "==> Packaging $buildOutput -> $archive"

    # tar.exe ships in Windows 10/11 and Windows Server 2019+. It understands
    # -czf and outputs gzip-compressed POSIX tar archives just like the bash
    # counterpart, so the .NET runtime extractor (which uses SharpZipLib /
    # System.Formats.Tar) sees the same payload regardless of build OS.
    & tar.exe -C $buildRoot -czf $archive $distroName
    if ($LASTEXITCODE -ne 0) {
        throw "tar exited with code $LASTEXITCODE"
    }

    $sizeMb = [math]::Round((Get-Item $archive).Length / 1MB, 1)
    Write-Host ("==> Done. Archive size: {0} MiB" -f $sizeMb)
    Write-Host "    Run 'dotnet build dotnet/OpenVSCodeServer.slnx' to embed it."
}
finally {
    Pop-Location
}
