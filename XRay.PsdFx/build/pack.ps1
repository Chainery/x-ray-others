<#
.SYNOPSIS
    Packs X-Ray.PsdFx (net45 + netstandard2.0) into the local offline NuGet feed.

.DESCRIPTION
    The .nupkg lands in <repo>/packages, which nuget.config already lists as the
    "local" source, so a consumer can restore with no network at all:

        <PackageReference Include="X-Ray.PsdFx" Version="0.1.0-alpha" />

    Re-packing the same version does not refresh an already restored consumer:
    NuGet caches by version in %USERPROFILE%\.nuget\packages. Pass -Bump to
    publish a new build number, or -ClearCache to delete the cached copy.

.PARAMETER Version
    Package version. Default 0.1.0-alpha.

.PARAMETER Bump
    Append +N build metadata is useless for caching; this instead increments the
    last numeric part of a 3+ part version. Ignored for pre-release versions.

.PARAMETER ClearCache
    Removes the packaged version from the global packages folder after packing.

.EXAMPLE
    .\build\pack.ps1 -Version 0.2.0

.EXAMPLE
    .\build\pack.ps1 -Bump -ClearCache
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0-alpha',
    [string]$Configuration = 'Release',
    [switch]$Bump,
    [switch]$ClearCache
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$feed = Join-Path $root 'packages'
$project = Join-Path $root 'src\XRay.PsdFx\XRay.PsdFx.csproj'

if ($Bump -and $Version -notmatch '-') {
    $parts = $Version.Split('.')
    if ($parts.Count -ge 3) {
        [int]$last = $parts[$parts.Count - 1]
        $parts[$parts.Count - 1] = [string]($last + 1)
        $Version = $parts -join '.'
    }
}

New-Item -ItemType Directory -Force -Path $feed | Out-Null

Write-Host "Packing X-Ray.PsdFx $Version (net45 + netstandard2.0) -> $feed"
dotnet pack $project -c $Configuration -p:Version=$Version -o $feed --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet pack failed with exit code $LASTEXITCODE"
}

$package = Join-Path $feed "X-Ray.PsdFx.$Version.nupkg"
if (-not (Test-Path $package)) {
    throw "Expected package was not produced: $package"
}

if ($ClearCache) {
    $cached = Join-Path $env:USERPROFILE ".nuget\packages\x-ray.psdfx\$($Version.ToLowerInvariant())"
    if (Test-Path $cached) {
        Remove-Item $cached -Recurse -Force
        Write-Host "Cleared cache: $cached"
    }
}

Write-Host ""
Write-Host "Done: $package"
Write-Host "Consume it offline with:"
Write-Host "  <PackageReference Include=`"X-Ray.PsdFx`" Version=`"$Version`" />"
