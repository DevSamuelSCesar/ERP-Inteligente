<#
.SYNOPSIS
  Builds the Nexus installers (feature 9): a self-contained, single-file app per platform.

.DESCRIPTION
  win-x64   -> installer/out/win-x64/Nexus-<version>-win-x64.zip and, when Inno Setup 6 (ISCC.exe) is installed,
               installer/out/Nexus-<version>-setup.exe (per-user install, no administrator rights).
  linux-x64 -> installer/out/Nexus-<version>-linux-x64.tar.gz with install.sh and the desktop entry.
  macOS is not built: the macOS key store (Keychain) is not supported yet and the app refuses to start there.

.EXAMPLE
  ./installer/build.ps1                          # win-x64
  ./installer/build.ps1 -Runtimes win-x64,linux-x64 -Version 1.0.0
#>
param(
    [string[]] $Runtimes = @('win-x64'),
    [string] $Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $PSScriptRoot 'out'
$supported = @('win-x64', 'linux-x64')

foreach ($rid in $Runtimes) {
    if ($supported -notcontains $rid) { throw "Unsupported runtime '$rid'. Use: $($supported -join ', ')." }
    $publish = Join-Path $out "publish-$rid"
    if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }

    dotnet publish (Join-Path $root 'erp_inteligente.csproj') -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
        -p:Version=$Version -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid." }
    # Native debug symbols of the graphics packages (~100 MB) are not needed by users.
    Get-ChildItem $publish -Filter *.pdb -Recurse | Remove-Item -Force

    if ($rid -eq 'win-x64') {
        $zip = Join-Path $out "Nexus-$Version-win-x64.zip"
        if (Test-Path $zip) { Remove-Item $zip }
        Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
        Write-Host "Portable package: $zip"

        $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
            Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($iscc) {
            & $iscc "/DAppVersion=$Version" "/DPublishDir=$publish" "/O$out" (Join-Path $PSScriptRoot 'nexus.iss')
            if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }
        }
        else {
            Write-Host 'Inno Setup 6 not found: only the portable zip was built (install it to get the setup.exe).'
        }
    }
    else {
        $stage = Join-Path $out "nexus-$Version-$rid"
        if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
        New-Item -ItemType Directory -Force $stage | Out-Null
        Copy-Item (Join-Path $publish '*') $stage -Recurse
        Copy-Item (Join-Path $PSScriptRoot 'linux/install.sh'), (Join-Path $PSScriptRoot 'linux/nexus.desktop') $stage
        $tar = Join-Path $out "Nexus-$Version-$rid.tar.gz"
        tar -czf $tar -C $out (Split-Path -Leaf $stage)
        if ($LASTEXITCODE -ne 0) { throw 'tar failed.' }
        Write-Host "Linux package: $tar"
    }
}
