<#
.SYNOPSIS
    Downloads, SHA-256 verifies, and stages pinned baseline multimedia tools
    (yt-dlp, FFmpeg/FFprobe GPL-shared from yt-dlp/FFmpeg-Builds, and Deno)
    into BaselineTools\ for bundling into YTDownloadTool builds and releases.
#>
[CmdletBinding()]
param(
    [string]$YtDlpTag = "2026.10.26",
    [string]$DenoTag = "v2.9.8",
    [string]$FFmpegTag = "latest",
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$repoRoot = Split-Path -Parent $PSScriptRoot
$baselineRoot = Join-Path $repoRoot "BaselineTools"
$tempRoot = Join-Path $repoRoot "tools-cache"

$ytDlpDir = Join-Path $baselineRoot "yt-dlp"
$ffmpegDir = Join-Path $baselineRoot "ffmpeg"
$denoDir = Join-Path $baselineRoot "deno"

if (-not $Force -and
    (Test-Path (Join-Path $ytDlpDir "yt-dlp.exe")) -and
    (Test-Path (Join-Path $ffmpegDir "ffmpeg.exe")) -and
    (Test-Path (Join-Path $ffmpegDir "ffprobe.exe")) -and
    (Test-Path (Join-Path $denoDir "deno.exe"))) {
    Write-Host "BaselineTools already staged. Pass -Force to re-download." -ForegroundColor Green
    exit 0
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-RemoteText([string]$url) {
    $resp = Invoke-WebRequest -Uri $url -UseBasicParsing
    if ($resp.Content -is [byte[]]) {
        return [System.Text.Encoding]::UTF8.GetString($resp.Content)
    }
    return [string]$resp.Content
}

function Get-ExpectedHash([string]$checksumText, [string]$fileName) {
    $lines = @($checksumText -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" })
    foreach ($line in $lines) {
        $parts = @($line -split "\s+")
        if ($parts.Count -ge 2 -and $parts[0] -match "^[0-9a-fA-F]{64}$") {
            $name = $parts[-1].TrimStart('*')
            if ($name -ieq $fileName -or [System.IO.Path]::GetFileName($name) -ieq $fileName) {
                return $parts[0].ToLowerInvariant()
            }
        }
    }
    # PowerShell Get-FileHash | Format-List output (used by denoland/deno Windows .sha256sum)
    $psHash = $null
    $psPath = $null
    foreach ($line in $lines) {
        if ($line -match "^Hash\s*:\s*([0-9a-fA-F]{64})$") { $psHash = $Matches[1].ToLowerInvariant() }
        elseif ($line -match "^Path\s*:\s*(.+)$") { $psPath = $Matches[1].Trim() }
    }
    if ($null -ne $psHash -and ([string]::IsNullOrWhiteSpace($psPath) -or [System.IO.Path]::GetFileName($psPath) -ieq $fileName)) {
        return $psHash
    }

    if ($lines.Count -eq 1 -and $lines[0] -match "^[0-9a-fA-F]{64}$") {
        return $lines[0].ToLowerInvariant()
    }
    throw "Could not find SHA-256 entry for '$fileName'."
}

function Assert-FileSha256([string]$filePath, [string]$expectedHash) {
    $actual = (Get-FileHash -Path $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expectedHash.ToLowerInvariant()) {
        throw "SHA-256 mismatch for '$filePath'! Expected=$expectedHash Actual=$actual"
    }
    Write-Host "  [OK] SHA-256 verified: $actual" -ForegroundColor DarkGreen
}

New-Item -ItemType Directory -Force -Path $tempRoot, $ytDlpDir, $ffmpegDir, $denoDir | Out-Null

try {
    # 1. yt-dlp
    Write-Host "Fetching yt-dlp ($YtDlpTag)..." -ForegroundColor Cyan
    $ytUrl = "https://github.com/yt-dlp/yt-dlp/releases/download/$YtDlpTag/yt-dlp.exe"
    $ytSumUrl = "https://github.com/yt-dlp/yt-dlp/releases/download/$YtDlpTag/SHA2-256SUMS"
    $ytSums = Get-RemoteText $ytSumUrl
    $ytExpected = Get-ExpectedHash $ytSums "yt-dlp.exe"
    $ytTmp = Join-Path $tempRoot "yt-dlp.exe"
    Invoke-WebRequest -Uri $ytUrl -OutFile $ytTmp -UseBasicParsing
    Assert-FileSha256 $ytTmp $ytExpected
    Copy-Item $ytTmp (Join-Path $ytDlpDir "yt-dlp.exe") -Force

    # 2. FFmpeg / FFprobe (yt-dlp/FFmpeg-Builds GPL-shared)
    Write-Host "Fetching FFmpeg GPL-shared ($FFmpegTag)..." -ForegroundColor Cyan
    $ffAsset = "ffmpeg-master-latest-win64-gpl-shared.zip"
    $ffBase = if ($FFmpegTag -eq "latest") { "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download" } else { "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/$FFmpegTag" }
    $ffUrl = "$ffBase/$ffAsset"
    $ffSumUrl = "$ffBase/checksums.sha256"
    $ffSums = Get-RemoteText $ffSumUrl
    $ffExpected = Get-ExpectedHash $ffSums $ffAsset
    $ffZip = Join-Path $tempRoot $ffAsset
    Invoke-WebRequest -Uri $ffUrl -OutFile $ffZip -UseBasicParsing
    Assert-FileSha256 $ffZip $ffExpected

    Get-ChildItem $ffmpegDir -Force | Remove-Item -Recurse -Force
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ffZip)
    try {
        foreach ($entry in $zip.Entries) {
            if ([string]::IsNullOrEmpty($entry.Name)) { continue }
            $norm = $entry.FullName.Replace('\', '/')
            if ($norm.Contains("..")) { throw "Unsafe zip path: $($entry.FullName)" }

            $destFile = $null
            if ($norm -match "(^|/)bin/(ffmpeg\.exe|ffprobe\.exe|[^/]+\.dll)$") {
                $destFile = Join-Path $ffmpegDir $entry.Name
            }
            elseif ($entry.Name -ieq "LICENSE.txt" -or $entry.Name -ieq "COPYING.GPLv3") {
                $destFile = Join-Path $ffmpegDir "LICENSE.txt"
            }

            if ($null -ne $destFile) {
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destFile, $true)
            }
        }
    }
    finally {
        $zip.Dispose()
    }

    # 3. Deno
    Write-Host "Fetching Deno ($DenoTag)..." -ForegroundColor Cyan
    $denoAsset = "deno-x86_64-pc-windows-msvc.zip"
    $denoUrl = "https://github.com/denoland/deno/releases/download/$DenoTag/$denoAsset"
    $denoSumUrl = "https://github.com/denoland/deno/releases/download/$DenoTag/$denoAsset.sha256sum"
    $denoSums = Get-RemoteText $denoSumUrl
    $denoExpected = Get-ExpectedHash $denoSums $denoAsset
    $denoZip = Join-Path $tempRoot $denoAsset
    Invoke-WebRequest -Uri $denoUrl -OutFile $denoZip -UseBasicParsing
    Assert-FileSha256 $denoZip $denoExpected

    Get-ChildItem $denoDir -Force | Remove-Item -Recurse -Force
    $dzip = [System.IO.Compression.ZipFile]::OpenRead($denoZip)
    try {
        foreach ($entry in $dzip.Entries) {
            if ($entry.Name -ieq "deno.exe") {
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $denoDir "deno.exe"), $true)
            }
        }
    }
    finally {
        $dzip.Dispose()
    }

    # 4. Health probes
    Write-Host "Running pre-package health probes..." -ForegroundColor Cyan
    $ytVer = (& (Join-Path $ytDlpDir "yt-dlp.exe") --version).Trim()
    $ffVer = ((& (Join-Path $ffmpegDir "ffmpeg.exe") -version) | Select-Object -First 1).Trim()
    $fpVer = ((& (Join-Path $ffmpegDir "ffprobe.exe") -version) | Select-Object -First 1).Trim()
    $dnVer = ((& (Join-Path $denoDir "deno.exe") --version) | Select-Object -First 1).Trim()

    if ($LASTEXITCODE -ne 0) { throw "One or more baseline tool health probes failed." }

    # 5. Write provenance record for GPLv3 & supply-chain transparency
    $provenance = @"
YTDownloadTool Baseline Tools Provenance
Generated UTC: $((Get-Date).ToUniversalTime().ToString("o"))

[yt-dlp]
Tag: $YtDlpTag
Version: $ytVer
URL: $ytUrl
SHA-256: $ytExpected
License: The Unlicense (https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE)

[FFmpeg / FFprobe]
Source: yt-dlp/FFmpeg-Builds ($FFmpegTag)
Asset: $ffAsset
URL: $ffUrl
SHA-256: $ffExpected
FFmpeg: $ffVer
FFprobe: $fpVer
License: GPLv3 (see BaselineTools\ffmpeg\LICENSE.txt; build scripts & source references at https://github.com/yt-dlp/FFmpeg-Builds)

[Deno]
Tag: $DenoTag
Version: $dnVer
URL: $denoUrl
SHA-256: $denoExpected
License: MIT (https://github.com/denoland/deno/blob/main/LICENSE.md)
"@
    Set-Content -Path (Join-Path $baselineRoot "BASELINE_PROVENANCE.txt") -Value $provenance -Encoding UTF8

    Write-Host "`nBaselineTools staged and verified successfully!" -ForegroundColor Green
    Write-Host "  yt-dlp : $ytVer"
    Write-Host "  FFmpeg : $ffVer"
    Write-Host "  Deno   : $dnVer"
}
finally {
    if (Test-Path $tempRoot) {
        Remove-Item -Recurse -Force $tempRoot -ErrorAction SilentlyContinue
    }
}
