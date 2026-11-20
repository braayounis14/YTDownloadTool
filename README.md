# YTDownloadTool

A production-ready Windows desktop (.NET 10 WPF) application for downloading YouTube videos and recording YouTube livestreams, with fully automated multimedia engine management and delta auto-updates.

## Key Features

- **Dynamic Format Discovery:** Probes actual available resolutions (360p–2160p+), frame rates, HDR, codecs, and audio-only formats directly from YouTube rather than hardcoded lists.
- **Livestream Recording (Modes A, B & C):**
  - **Mode A (Record from now until end):** Starts from the current live edge using crash-safe MPEG-TS/MKV recording with automatic reconnection and clean `Stop & Save` finalization.
  - **Mode B (From start until end — DVR):** Uses `yt-dlp --live-from-start` when YouTube exposes DVR segments, automatically warning when DVR is unavailable or capped.
  - **Mode C (From start, stop near live edge):** Tracks all concurrent DASH streams via `ModeCLiveEdgeWatchdog`, stopping gracefully at a configurable live-edge margin (15s / **30s default** / 60s / 120s) via `CTRL_C_EVENT` so `yt-dlp`/FFmpeg merges cleanly into a playable file.
- **Zero-Manual-Setup Multimedia Engine (Hybrid Option C):**
  - Bundles verified baseline `yt-dlp.exe`, `ffmpeg.exe` + `ffprobe.exe` (`yt-dlp/FFmpeg-Builds` GPL-shared), and `deno.exe` (required by `yt-dlp >= 2025.11.12` EJS).
  - Updates each tool independently over allowlisted HTTPS GitHub releases with mandatory SHA-256 verification, Zip-Slip protection, pre-activation health probes, atomic manifest swap, rollback, and baseline self-healing.
- **Single-Click Windows Installer & Delta Updates:**
  - Self-contained `win-x64` `.NET 10` (ReadyToRun) packaged via **Velopack** (`YTDownloadTool-win-Setup.exe`) — no `.NET`, Python, FFmpeg, or administrator/UAC prompt required.
  - Background delta app updates from GitHub Releases (`braayounis14/YTDownloadTool`) with 12-hour rate-limit throttling and silent HTTP 403 handling.
  - Clean uninstallation removes managed tool caches (`%LocalAppData%\YTDownloadToolData`) while **never touching user downloaded videos**.

## Repository Layout

- `YTDownloadTool.slnx` — Unified .NET 10 solution
- `YTDownloadTool.Core/` — Core domain engine (`Process/`, `Tools/`, `Downloads/`, `Livestream/`, `Settings/`, `Logging/`, `Paths/`)
- `YTDownloadTool/` — WPF desktop client (`Program.cs`, `App.xaml.cs`, `MainWindow`, `Views/`, `ViewModels/`, `Services/`, `Converters/`)
- `YTDownloadTool.Tests/` — Automated xUnit unit & Windows process integration test suite
- `scripts/fetch-baseline-tools.ps1` — Downloads, SHA-256 verifies, and stages pinned `BaselineTools/`
- `scripts/build-release.ps1` — Full local test → publish → Velopack packaging pipeline
- `.github/workflows/` — Automated CI (`ci.yml`) and tagged GitHub Release packaging (`release.yml`)
- `THIRD_PARTY_NOTICES.txt` — Open-source licenses & GPLv3 FFmpeg source compliance notices

## Building & Testing Locally

```powershell
# Build & run all automated tests
dotnet build YTDownloadTool.slnx -c Release
dotnet test YTDownloadTool.slnx -c Release

# Stage verified baseline multimedia tools (yt-dlp, FFmpeg/FFprobe, Deno)
.\scripts\fetch-baseline-tools.ps1

# Build full self-contained win-x64 Velopack installer & release feed
.\scripts\build-release.ps1 -Version "1.0.0"
```

## Releasing a New Application Version

1. Commit and push verified changes to `main`.
2. Tag and push a semantic version tag (e.g., `git tag v1.0.1 && git push origin v1.0.1`), or run `.\scripts\build-release.ps1 -Version "1.0.1"` locally and upload `artifacts\Releases\*` to a GitHub Release.
3. Installed clients automatically discover the update on their next throttled background check (or manual check in Settings), download the binary delta patch, and prompt to restart safely once no downloads are active.

## Third-Party Software & Licenses

See [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) for complete licensing, attribution, and GPLv3 corresponding source information for FFmpeg (`yt-dlp/FFmpeg-Builds`), `yt-dlp` (The Unlicense), `Deno` (MIT), and `.NET`/NuGet packages.
