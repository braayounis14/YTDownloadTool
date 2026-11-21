# Tidejar — Video & Stream Saver

A modern, privacy-first Windows desktop application (.NET 10 WPF) for saving online videos, audio tracks, and live broadcasts for offline personal viewing — featuring a persistent SQLite download queue, livestream recording modes, media integrity verification, and an all-in-one bundled multimedia engine.

### Download for Windows

- **GitHub Desktop Edition (Installer & Portable ZIP):** [**Download Latest Release for Windows (x64)**](https://github.com/braayounis14/YTDownloadTool/releases/latest)
- **Microsoft Store Edition (MSIX):** Available as a packaged Windows app (`BaraaYounis.Tidejar`) with a bundled, read-only multimedia engine and updates managed exclusively by the Microsoft Store.

> Requires Windows 10 (1809+) or Windows 11 (64-bit). Self-contained — no separate `.NET`, Python, or FFmpeg installation required.

---

## Key Features (v1.1.0)

- **Real Format Inspection & Video/Audio Saving:** Probes available video resolutions (up to 4K/HDR and high frame rates), codecs, and audio-only streams directly before download, with container selection (`MP4`, `MKV`, `WEBM`, `M4A`, `MP3`, `OPUS`).
- **Livestream Recording (Modes A, B & C):**
  - **Mode A — Record from now until end:** Crash-safe recording from the current live edge with clean **Stop & Save** finalization.
  - **Mode B — Record from start until end (DVR):** Captures from the beginning of DVR-enabled broadcasts and warns automatically if DVR is unavailable or capped.
  - **Mode C — Record from start, stop near live edge:** Captures from stream start and stops gracefully at a configurable live-edge margin (15s / 30s default / 60s / 120s) so video and audio merge into a cleanly playable file.
- **Persistent SQLite Queue & Crash Recovery:**
  - Local SQLite database (WAL mode) with transactional schema migrations and automatic pre-migration backups.
  - **Pause & Resume** for VOD downloads (`--continue`), queue reordering (`TOP` / `▲` / `▼`), automatic restoration of interrupted downloads across restarts, and **Recover & Save Partial** for interrupted livestreams.
  - Concurrent disk-space reservation ledger to prevent mid-download storage exhaustion.
- **Per-Download Speed Limiting & Concurrency Control:** Apply bandwidth caps per active download (`--limit-rate`) and adjust maximum concurrent jobs in Settings.
- **Media Integrity Verification & Lossless Repair:** Inspect saved files in the Library with `ffprobe` (video/audio streams, codecs, duration, container health) and run lossless FFmpeg remux repair when metadata needs fixing.
- **Local Diagnostics & Redacted Support Export:** Monitor SQLite health and reclaim unused temporary staging folders safely, or opt in to export a local `.zip` diagnostic bundle with tokens, cookies, and Windows user profile paths automatically redacted.

---

## Windows Editions

| Edition | Packaging | Updates & Multimedia Engine |
| --- | --- | --- |
| **Microsoft Store** | Signed MSIX (`BaraaYounis.Tidejar`) | Bundled read-only engine (`yt-dlp`, `FFmpeg`/`FFprobe`, `Deno`); zero runtime binary downloads; all updates delivered by the Microsoft Store. |
| **GitHub Desktop** | Self-contained Velopack `Setup.exe` & Portable `.zip` | Optional GitHub Releases delta app updates and SHA-256-verified engine updates with automatic rollback & self-healing. |

---

## Privacy, Licenses & Legal Notice

- **Privacy Policy:** Tidejar collects **zero** telemetry, analytics, or crash beacons and requires no user account. Read the full policy in [PRIVACY.md](PRIVACY.md).
- **Third-Party Software & Open-Source Licenses:** Tidejar invokes standalone third-party command-line utilities (`FFmpeg`/`FFprobe` via `yt-dlp/FFmpeg-Builds` under **GPLv3**, `yt-dlp` under The Unlicense, and `Deno` under MIT) strictly out-of-process. Full license texts, attribution, and GPLv3 corresponding source details are listed in [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).
- **Independent Utility & Personal Use Disclaimer:** Tidejar is an independent third-party desktop utility published by Baraa Younis and is not affiliated with, endorsed by, or sponsored by YouTube or Google LLC. It is intended solely for saving content that you own, have explicit permission to download, or are legally permitted to archive for personal, non-commercial offline use. Users are solely responsible for complying with applicable copyright laws and the terms of service of any website or media platform they access.

---

## Building from Source

Requires the **.NET 10 SDK** on Windows (`x64`):

```powershell
# Build and run the automated test suite (Desktop profile)
dotnet build YTDownloadTool.slnx -c Release
dotnet test YTDownloadTool.slnx -c Release --no-build

# Build and test the Microsoft Store (MSIX) profile
dotnet build YTDownloadTool.slnx -c Release -p:IsStoreBuild=true
dotnet test YTDownloadTool.slnx -c Release --no-build -p:IsStoreBuild=true
```

Packaging scripts for staging verified baseline tools (`scripts/fetch-baseline-tools.ps1`), building the GitHub Desktop installer (`scripts/build-release.ps1`), and building the Microsoft Store MSIX package (`scripts/build-msix.ps1`) are located in `scripts/`.
