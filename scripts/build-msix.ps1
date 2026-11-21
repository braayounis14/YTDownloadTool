<#
.SYNOPSIS
    Builds, tests, publishes self-contained win-x64 Tidejar with IsStoreBuild=true
    (zero Velopack / zero runtime tool downloads), validates Store compliance artifacts,
    generates resources.pri, and packs an MSIX package ready for Microsoft Partner Center
    (plus an optional test-signed sideload .msix for local verification).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$IdentityName = "BaraaYounis.Tidejar",
    [string]$Publisher = "CN=B7339C9F-C5CE-41FC-A57D-C180B63088F4",
    [string]$PublisherDisplayName = "Baraa Younis",
    [string]$DisplayName = "Tidejar - Video & Stream Saver",

    [switch]$SkipTests,
    [switch]$SkipToolFetch,
    [switch]$TestSign,
    [string]$PfxPath = "",
    [string]$PfxPassword = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Normalize and enforce 4-part Microsoft Store version ending in .0 (e.g., 1.1.0 -> 1.1.0.0)
$cleanVer = $Version.TrimStart('v', 'V')
$verParts = @($cleanVer.Split('.'))
if ($verParts.Count -eq 3) {
    $storeVersion = "$cleanVer.0"
    $threePartVersion = $cleanVer
}
elseif ($verParts.Count -eq 4) {
    if ($verParts[3] -ne "0") {
        throw "Microsoft Store requires the 4th version segment to be 0 (got '$cleanVer')."
    }
    $storeVersion = $cleanVer
    $threePartVersion = "$($verParts[0]).$($verParts[1]).$($verParts[2])"
}
else {
    throw "Version must be 3-part (1.1.0) or 4-part (1.1.0.0); got '$Version'."
}

function Find-WindowsSdkTool([string]$toolName) {
    $cmd = Get-Command $toolName -ErrorAction SilentlyContinue
    if ($null -ne $cmd) { return $cmd.Source }

    $searchRoots = @(
        "C:\Program Files (x86)\Windows Kits\10\bin",
        "C:\Program Files\Windows Kits\10\bin",
        (Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windows.sdk.buildtools")
    )

    foreach ($root in $searchRoots) {
        if (-not (Test-Path $root)) { continue }
        $found = @(Get-ChildItem -Path $root -Recurse -Filter $toolName -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like "*\x64\*" } |
            Sort-Object FullName -Descending)
        if ($found.Count -gt 0) {
            return $found[0].FullName
        }
    }

    throw "Could not locate '$toolName'. Install the Windows 10/11 SDK or restore Microsoft.Windows.SDK.BuildTools."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$slnPath = Join-Path $repoRoot "YTDownloadTool.slnx"
$appProj = Join-Path $repoRoot "YTDownloadTool\YTDownloadTool.csproj"
$manifestTemplatePath = Join-Path $repoRoot "Packaging\Package.appxmanifest"
$assetsSourceDir = Join-Path $repoRoot "Packaging\Assets"
$stageDir = Join-Path $repoRoot "artifacts\store-stage\win-x64"
$storeOutDir = Join-Path $repoRoot "artifacts\Store"

$makeAppx = Find-WindowsSdkTool "makeappx.exe"
$makePri = Find-WindowsSdkTool "makepri.exe"
$signTool = if ($TestSign -or -not [string]::IsNullOrWhiteSpace($PfxPath)) { Find-WindowsSdkTool "signtool.exe" } else { $null }

Write-Host "Using MakeAppx : $makeAppx" -ForegroundColor DarkCyan
Write-Host "Using MakePri  : $makePri" -ForegroundColor DarkCyan
if ($null -ne $signTool) { Write-Host "Using SignTool : $signTool" -ForegroundColor DarkCyan }

Push-Location $repoRoot
try {
    if (-not $SkipTests) {
        Write-Host "`n=== 1. Build Solution & Run Test Suite (IsStoreBuild=true) ===" -ForegroundColor Cyan
        dotnet build $slnPath -c Release -p:IsStoreBuild=true --nologo
        if ($LASTEXITCODE -ne 0) { throw "Store solution build failed." }

        dotnet test $slnPath -c Release --no-build -p:IsStoreBuild=true --nologo --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw "Store test suite failed." }
    }

    if (-not $SkipToolFetch) {
        Write-Host "`n=== 2. Stage & Verify Baseline Multimedia Tools ===" -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot "fetch-baseline-tools.ps1")
        if ($LASTEXITCODE -ne 0) { throw "Baseline tool fetch failed." }
    }

    Write-Host "`n=== 3. Publish Self-Contained win-x64 Store Build (IsStoreBuild=true) ===" -ForegroundColor Cyan
    if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

    dotnet publish $appProj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:IsStoreBuild=true `
        -p:PublishSingleFile=false `
        -p:PublishReadyToRun=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:Version=$threePartVersion `
        -p:AssemblyVersion=$storeVersion `
        -p:FileVersion=$storeVersion `
        -o $stageDir `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (IsStoreBuild=true) failed." }

    # Store Policy Gate: confirm ZERO Velopack self-updater binaries exist in Store payload
    $forbidden = @("Velopack.dll", "Update.exe", "Squirrel.dll")
    foreach ($bad in $forbidden) {
        $badMatches = @(Get-ChildItem -Path $stageDir -Recurse -Filter $bad -ErrorAction SilentlyContinue)
        if ($badMatches.Count -gt 0) {
            throw "Store Policy 10.2.5 violation: forbidden self-updater file '$bad' found in Store publish directory!"
        }
    }

    # Verify required bundled tools, native SQLite runtime, privacy policy, and GPLv3 compliance files
    $required = @(
        (Join-Path $stageDir "YTDownloadTool.exe"),
        (Join-Path $stageDir "e_sqlite3.dll"),
        (Join-Path $stageDir "PRIVACY.md"),
        (Join-Path $stageDir "THIRD_PARTY_NOTICES.txt"),
        (Join-Path $stageDir "BaselineTools\yt-dlp\yt-dlp.exe"),
        (Join-Path $stageDir "BaselineTools\ffmpeg\ffmpeg.exe"),
        (Join-Path $stageDir "BaselineTools\ffmpeg\ffprobe.exe"),
        (Join-Path $stageDir "BaselineTools\ffmpeg\LICENSE.txt"),
        (Join-Path $stageDir "BaselineTools\deno\deno.exe"),
        (Join-Path $stageDir "BaselineTools\BASELINE_PROVENANCE.txt"),
        (Join-Path $stageDir "BaselineTools\baseline-checksums.json")
    )
    foreach ($req in $required) {
        if (-not (Test-Path $req)) {
            throw "Required Store artifact missing from publish output: $req"
        }
    }

    Write-Host "`n=== 4. Stage Visual Assets, AppxManifest.xml & resources.pri ===" -ForegroundColor Cyan
    if (-not (Test-Path $assetsSourceDir)) {
        throw "Visual assets directory not found: $assetsSourceDir. Run scripts/generate-store-assets.ps1 first."
    }

    $stageAssetsDir = Join-Path $stageDir "Assets"
    New-Item -ItemType Directory -Force -Path $stageAssetsDir | Out-Null
    Copy-Item -Path (Join-Path $assetsSourceDir "*") -Destination $stageAssetsDir -Recurse -Force

    # Stamp parameterized Package.appxmanifest -> AppxManifest.xml
    $manifestXml = Get-Content -Path $manifestTemplatePath -Raw -Encoding UTF8
    $manifestXml = $manifestXml.Replace("__IDENTITY_NAME__", [System.Security.SecurityElement]::Escape($IdentityName))
    $manifestXml = $manifestXml.Replace("__PUBLISHER__", [System.Security.SecurityElement]::Escape($Publisher))
    $manifestXml = $manifestXml.Replace("__VERSION__", [System.Security.SecurityElement]::Escape($storeVersion))
    $manifestXml = $manifestXml.Replace("__DISPLAY_NAME__", [System.Security.SecurityElement]::Escape($DisplayName))
    $manifestXml = $manifestXml.Replace("__PUBLISHER_DISPLAY_NAME__", [System.Security.SecurityElement]::Escape($PublisherDisplayName))
    Set-Content -Path (Join-Path $stageDir "AppxManifest.xml") -Value $manifestXml -Encoding UTF8

    # Generate a single unified resources.pri indexing Assets\ cleanly (no split .scale/.language PRI files
    # that Partner Center rejects in single-MSIX submissions, and no false language indexing of .NET satellite dirs).
    $priConfigPath = Join-Path $repoRoot "artifacts\store-stage\priconfig.xml"
    $priConfigXml = @"
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<resources targetOsVersion="10.0.0" majorVersion="1">
  <index root="\" startIndexAt="\Assets">
    <default>
      <qualifier name="Language" value="en-US"/>
      <qualifier name="Contrast" value="standard"/>
      <qualifier name="Scale" value="100"/>
      <qualifier name="HomeRegion" value="001"/>
      <qualifier name="TargetSize" value="256"/>
      <qualifier name="LayoutDirection" value="LTR"/>
      <qualifier name="Theme" value="dark"/>
      <qualifier name="AlternateForm" value=""/>
      <qualifier name="DXFeatureLevel" value="DX9"/>
      <qualifier name="Configuration" value=""/>
      <qualifier name="DeviceFamily" value="Universal"/>
      <qualifier name="Custom" value=""/>
    </default>
    <indexer-config type="folder" foldernameAsQualifier="false" filenameAsQualifier="true" qualifierDelimiter="."/>
  </index>
</resources>
"@
    Set-Content -Path $priConfigPath -Value $priConfigXml -Encoding UTF8

    & $makePri new /pr $stageDir /cf $priConfigPath /mn (Join-Path $stageDir "AppxManifest.xml") /of (Join-Path $stageDir "resources.pri") /o
    if ($LASTEXITCODE -ne 0) { throw "makepri new failed." }

    Write-Host "`n=== 5. Pack & Strictly Validate MSIX Package ===" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $storeOutDir | Out-Null
    $storeMsixPath = Join-Path $storeOutDir "Tidejar-$storeVersion-x64.msix"
    if (Test-Path $storeMsixPath) { Remove-Item -Force $storeMsixPath }

    & $makeAppx pack /d $stageDir /p $storeMsixPath /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed." }

    Write-Host "Created Store-uploadable MSIX: $storeMsixPath" -ForegroundColor Green

    if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
        Write-Host "`n=== 6. Sign MSIX with Provided PFX ===" -ForegroundColor Cyan
        $signedPath = Join-Path $storeOutDir "Tidejar-$storeVersion-x64-signed.msix"
        Copy-Item $storeMsixPath $signedPath -Force
        & $signTool sign /fd SHA256 /a /f $PfxPath /p $PfxPassword $signedPath
        if ($LASTEXITCODE -ne 0) { throw "signtool sign (PFX) failed." }
    }
    elseif ($TestSign) {
        Write-Host "`n=== 6. Test-Sign Local Sideload MSIX (Dev Certificate) ===" -ForegroundColor Cyan
        $sideloadPath = Join-Path $storeOutDir "Tidejar-$storeVersion-x64-sideload.msix"
        $cerPath = Join-Path $storeOutDir "Tidejar-DevTest.cer"
        Copy-Item $storeMsixPath $sideloadPath -Force

        $existingCert = @(Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
            Where-Object { $_.Subject -eq $Publisher -and $_.FriendlyName -eq "Tidejar Local Sideload Test Cert" } |
            Sort-Object NotAfter -Descending)

        if ($existingCert.Count -gt 0 -and $existingCert[0].NotAfter -gt (Get-Date).AddDays(7)) {
            $cert = $existingCert[0]
            Write-Host "Reusing local dev test cert: $($cert.Thumbprint)" -ForegroundColor DarkCyan
        }
        else {
            $cert = New-SelfSignedCertificate `
                -Type Custom `
                -Subject $Publisher `
                -KeyUsage DigitalSignature `
                -FriendlyName "Tidejar Local Sideload Test Cert" `
                -CertStoreLocation "Cert:\CurrentUser\My" `
                -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
            Write-Host "Created local dev test cert: $($cert.Thumbprint)" -ForegroundColor DarkCyan
        }

        Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
        & $signTool sign /fd SHA256 /sha1 $cert.Thumbprint $sideloadPath
        if ($LASTEXITCODE -ne 0) { throw "signtool sign (test cert) failed." }

        Write-Host "Created local sideload MSIX : $sideloadPath" -ForegroundColor Green
        Write-Host "Exported dev public cert    : $cerPath" -ForegroundColor Green
        Write-Host "NOTE: Upload the UNSIGNED '$storeMsixPath' to Microsoft Partner Center (Store signs it automatically)." -ForegroundColor Yellow
    }

    Write-Host "`n=== Store MSIX Artifacts in $storeOutDir ===" -ForegroundColor Green
    Get-ChildItem $storeOutDir -File | Select-Object Name, @{N='MB';E={[math]::Round($_.Length/1MB,2)}} | Format-Table -AutoSize
}
finally {
    Pop-Location
}
