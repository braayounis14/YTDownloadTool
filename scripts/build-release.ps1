<#
.SYNOPSIS
    Builds, tests, publishes self-contained win-x64 YTDownloadTool with verified
    BaselineTools, and packages a Velopack per-user Windows installer + update feed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$SkipTests,
    [switch]$SkipToolFetch,
    [string]$PreviousReleasesDir = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$slnPath = Join-Path $repoRoot "YTDownloadTool.slnx"
$appProj = Join-Path $repoRoot "YTDownloadTool\YTDownloadTool.csproj"
$publishDir = Join-Path $repoRoot "artifacts\publish\win-x64"
$releasesDir = Join-Path $repoRoot "artifacts\Releases"

Push-Location $repoRoot
try {
    Write-Host "=== 1. Restore local .NET tools (Velopack vpk) ===" -ForegroundColor Cyan
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed." }

    if (-not $SkipTests) {
        Write-Host "`n=== 2. Build Solution & Run Automated Test Suite ===" -ForegroundColor Cyan
        dotnet build $slnPath -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw "Solution build failed; aborting release build." }

        dotnet test $slnPath -c Release --no-build --nologo --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw "Automated tests failed; aborting release build." }
    }

    if (-not $SkipToolFetch) {
        Write-Host "`n=== 3. Stage & Verify Baseline Multimedia Tools ===" -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot "fetch-baseline-tools.ps1")
        if ($LASTEXITCODE -ne 0) { throw "Baseline tool fetch failed." }
    }

    Write-Host "`n=== 4. Publish Self-Contained win-x64 (ReadyToRun, Non-SingleFile) ===" -ForegroundColor Cyan
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

    dotnet publish $appProj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishReadyToRun=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:Version=$Version `
        -p:AssemblyVersion="$Version.0" `
        -p:FileVersion="$Version.0" `
        -o $publishDir `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

    # Verify required published artifacts & license compliance files
    $required = @(
        (Join-Path $publishDir "YTDownloadTool.exe"),
        (Join-Path $publishDir "THIRD_PARTY_NOTICES.txt"),
        (Join-Path $publishDir "BaselineTools\yt-dlp\yt-dlp.exe"),
        (Join-Path $publishDir "BaselineTools\ffmpeg\ffmpeg.exe"),
        (Join-Path $publishDir "BaselineTools\ffmpeg\ffprobe.exe"),
        (Join-Path $publishDir "BaselineTools\ffmpeg\LICENSE.txt"),
        (Join-Path $publishDir "BaselineTools\deno\deno.exe"),
        (Join-Path $publishDir "BaselineTools\BASELINE_PROVENANCE.txt"),
        (Join-Path $publishDir "BaselineTools\baseline-checksums.json")
    )
    foreach ($req in $required) {
        if (-not (Test-Path $req)) {
            throw "Required release artifact missing from publish output: $req"
        }
    }

    Write-Host "`n=== 5. Package Velopack Windows Installer & Delta Feed ===" -ForegroundColor Cyan
    if (Test-Path $releasesDir) { Remove-Item -Recurse -Force $releasesDir }
    New-Item -ItemType Directory -Force -Path $releasesDir | Out-Null

    if (-not [string]::IsNullOrWhiteSpace($PreviousReleasesDir) -and (Test-Path $PreviousReleasesDir)) {
        $prevFiles = @(Get-ChildItem -Path $PreviousReleasesDir -File -ErrorAction SilentlyContinue)
        if ($prevFiles.Count -gt 0) {
            Copy-Item -Path $prevFiles.FullName -Destination $releasesDir -Force
        }
    }

    dotnet vpk pack `
        --packId YTDownloadTool `
        --packVersion $Version `
        --packDir $publishDir `
        --mainExe YTDownloadTool.exe `
        --runtime win-x64 `
        --packTitle "YTDownloadTool" `
        --packAuthors "YTDownloadTool" `
        --outputDir $releasesDir

    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }

    # Copy provenance alongside Velopack outputs (THIRD_PARTY_NOTICES.txt lives at repo root and inside Setup.exe)
    Copy-Item (Join-Path $publishDir "BaselineTools\BASELINE_PROVENANCE.txt") $releasesDir -Force

    Write-Host "`n=== Release Artifacts Created in $releasesDir ===" -ForegroundColor Green
    Get-ChildItem $releasesDir | Select-Object Name, @{N='MB';E={[math]::Round($_.Length/1MB,2)}} | Format-Table -AutoSize
}
finally {
    Pop-Location
}
