# Privacy Policy — Tidejar - Video & Stream Saver

**Effective Date:** November 21, 2026  
**Publisher:** Baraa Younis  
**Application:** Tidejar - Video & Stream Saver

Tidejar is designed with privacy first. All downloading, media inspection, queue persistence, and integrity verification happen strictly on your Windows device.

---

## 1. Data We Do Not Collect

Tidejar does **not**:
- Collect telemetry, usage analytics, diagnostics beacons, or crash reports automatically.
- Include advertising SDKs, third-party trackers, or monetization scripts.
- Require user accounts, sign-in credentials, or cloud storage.
- Transmit your download history, library items, saved files, or settings to the publisher or any third-party analytics service.

---

## 2. Data Stored Locally on Your Device

To provide reliable queue management, pause/resume, and library tracking, Tidejar stores the following data **locally on your Windows PC only**:
- **Application Settings (`settings.json`):** Download folder path, preferred quality/container, concurrency limit, per-download speed cap, and livestream defaults.
- **Local SQLite Database (`ytdownloadtool.db` & pre-migration backups):** Active/queued/paused download jobs, completed library entries, and up to 200 recent redacted failure summaries.
- **Temporary Staging Files & Rolling Logs:** Partial download fragments while a job is active/paused/recoverable, and local diagnostic logs rotated automatically on disk.

You can clear library history, discard paused jobs, clean unused temporary files, or uninstall the app at any time. Uninstalling the Microsoft Store (MSIX) edition removes the app's packaged local application data automatically without deleting videos saved in your personal Videos/Downloads folder.

---

## 3. Outbound Network Connections

Tidejar initiates network connections solely in the following situations:
1. **User-Entered Media URLs:** When you explicitly paste a URL and click **Analyze** or **Download / Start Recording**, Tidejar connects directly to the target media service to retrieve available stream formats, thumbnails, and media data. Those requests are subject to the destination platform's own privacy policy and terms of service.
2. **Non-Store Desktop Edition Only (GitHub Velopack):** The standalone GitHub desktop edition optionally connects to official GitHub Releases endpoints (`github.com/braayounis14/YTDownloadTool`, `github.com/yt-dlp/yt-dlp`, `github.com/yt-dlp/FFmpeg-Builds`, `github.com/denoland/deno`) to check for and verify application and multimedia engine updates.
3. **Microsoft Store (MSIX) Edition:** Performs **zero** background update checks and downloads **zero** executable binaries at runtime. All application and bundled engine updates are delivered strictly by the Microsoft Store.

---

## 4. Diagnostic Bundle Export (Opt-In)

If you choose to click **Export Diagnostic Bundle (.zip)** in Settings, Tidejar creates a local `.zip` archive at the location you pick. It never uploads this file automatically. Before writing the archive, Tidejar automatically redacts authorization headers, Bearer tokens, cookies, signed query parameters, and Windows user profile paths (`C:\Users\<REDACTED>`).

---

## 5. Children's Privacy

Tidejar does not collect personal information from any users, including children under 13 (or the applicable age of digital consent in your jurisdiction).

---

## 6. Changes to This Policy & Contact

Updates to this Privacy Policy will be published alongside future releases at:  
https://github.com/braayounis14/YTDownloadTool/blob/main/PRIVACY.md

For privacy questions or support requests, contact the publisher via:  
https://github.com/braayounis14/YTDownloadTool/issues
