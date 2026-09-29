<#
.SYNOPSIS
  Builds, tests, publishes and (optionally) packages and signs a TICKSHIFT release.

.EXAMPLE
  ./build/publish.ps1                                   # test + publish win-x64 + zip (+ installer if Inno Setup is installed)
  ./build/publish.ps1 -Version 1.0.1 -CertThumbprint ABCD…   # also Authenticode-sign binaries and installer
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [ValidateSet("win-x64", "win-x86")] [string]$Runtime = "win-x64",
    [string]$CertThumbprint = $env:TICKSHIFT_SIGN_THUMBPRINT,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "artifacts/publish/$Runtime"
$installerDir = Join-Path $root "artifacts/installer"

Push-Location $root
try {
    Write-Host "== Static XAML checks" -ForegroundColor Cyan
    python build/check_xaml.py
    if ($LASTEXITCODE -ne 0) { throw "XAML checks failed" }

    if (-not $SkipTests) {
        Write-Host "== Tests" -ForegroundColor Cyan
        dotnet test Tickshift.sln -c Release
        if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
    }

    Write-Host "== Publish $Runtime ($Version)" -ForegroundColor Cyan
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish src/Tickshift.App/Tickshift.App.csproj -c Release -r $Runtime --self-contained true `
        -p:Version=$Version -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

    $signtool = $null
    if ($CertThumbprint) {
        $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
        if (-not $signtool) { throw "signtool.exe not found (install the Windows SDK)." }
        Write-Host "== Signing binaries with certificate $CertThumbprint" -ForegroundColor Cyan
        $targets = Get-ChildItem $publishDir -Include "Tickshift*.exe", "Tickshift*.dll" -Recurse
        & $signtool.FullName sign /sha1 $CertThumbprint /fd sha256 /tr $TimestampUrl /td sha256 $targets.FullName
        if ($LASTEXITCODE -ne 0) { throw "Signing failed" }
        & $signtool.FullName verify /pa $targets.FullName
    } else {
        Write-Warning "No signing certificate supplied: binaries are UNSIGNED. See docs/SIGNING.md."
    }

    Write-Host "== Portable zip" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $installerDir | Out-Null
    $zip = Join-Path $installerDir "TICKSHIFT-$Version-$Runtime-portable.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "$publishDir/*" -DestinationPath $zip

    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($iscc -and $Runtime -eq "win-x64") {
        Write-Host "== Installer" -ForegroundColor Cyan
        $isccArgs = @("/DAppVersion=$Version", "/DSourceDir=$publishDir", "installer/Tickshift.iss")
        if ($signtool) {
            $isccArgs = @("/Stickshift=`"$($signtool.FullName)`" sign /sha1 $CertThumbprint /fd sha256 /tr $TimestampUrl /td sha256 `$f", "/DSigned=1") + $isccArgs
        }
        & $iscc @isccArgs
        if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
    } else {
        Write-Warning "Inno Setup 6 not found; skipped installer (portable zip was created)."
    }

    Write-Host "Done. Output: $installerDir" -ForegroundColor Green
}
finally {
    Pop-Location
}
