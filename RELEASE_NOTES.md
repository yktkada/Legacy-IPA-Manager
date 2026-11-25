# Legacy IPA Manager Release

IPA library manager, compatibility analyzer, and multi-device installer for legacy iOS devices on Windows.

## v1.1.0

### Highlights
- **Unified Bilingual Application (`Strings.resx`)**: Merged the former separate English and Chinese source trees into a single bilingual build (`System` / `Simplified Chinese` / `English`, switchable live in `Settings -> Layout`).
- **Companion Offline Index & Downloader (`IdeviceIndex.exe`)**:
  - Browse massive offline IPA indexes (`200,000+` row virtualized grid or optional 500-row paging) with BundleId grouping, advanced multi-condition filtering, `93`-version iOS catalog quick filter, and remote `Index.UpdateUrl` sync.
  - Built-in `aria2c` (1.37.0) download engine with 4-step SHA-256 verification, resume support, ZIP validation, proxy modes (`system` / `manual` / `none`), strict write-boundary protection, and automatic named-pipe import (`IdeviceGui.Import.v1`) into the main library.
- **Unified Bottom Status Bar & Read-Only History**:
  - Always-visible bilingual status bar with 6-second sticky error protection, hover summary of the latest 5 notices, and a left-side `History (N)` read-only popup (newest first, up to 50 entries).
  - Automatic promotion of silent background/filesystem errors and off-UI drive reachability probing with circuit-breaker throttling when library drives disconnect.
- **UI & Out-of-the-Box Experience Polish**:
  - Log panel hidden by default for a cleaner two-column startup layout (toggleable anytime via the toolbar button or `Ctrl+L`).
  - Refined no-device states (hiding empty device tabs and `More` menu, disabling device-only actions, fallback queue header/suspend label, and two-state empty-queue guidance), fixed default group selection (`All`), and unified `24 px` toolbar control heights/spacing.
  - Added **0–9 / A–Z / #** Pinyin jump bar, left-gutter single-key shortcut badges (`Ctrl+Alt+<Key>`), per-device library scroll memory, live closing-step progress in the window title bar, and `WM_DEVICECHANGE` USB event detection with adaptive polling.
- **New Dual-Tier Application Icons**:
  - S-tier icons (`app.ico`) for desktop, taskbar, and Alt+Tab; L-tier icons (`icon_large.png` / `assets/`) for About dialogs, README, and User Guides (custom artwork created by the project owner with Gemini and ChatGPT).
- **License Update (`1.1.0+`)**:
  - First-party source code and custom assets starting with **v1.1.0** are licensed under the **PolyForm Noncommercial License 1.0.0** (`LICENSE`). Prior releases (`v1.0.0` / `v1.0.1`) remain `GPL-3.0-only`. Bundled `toolkit/` (`ideviceinstaller.exe`, `aria2c.exe`, `libimobiledevice`, etc.) and `third_party/` keep their original GPL-2.0 / LGPL-2.1 / permissive licenses (`THIRD_PARTY_NOTICES.md`).

### Supported Devices & Scope
- **Tested Hardware**: iPhone 4 (iOS 4.1), iPad 2 (iOS 6.1.3), iPhone 4S (iOS 7.1.1), iPhone 5 (iOS 8.1.2), iPod touch 5 (iOS 8.4.1)
- **iOS Scope**: iOS 4 / 6 / 7 / 8 and iPad verified; iOS 9 / 10 untested
- **Requirement**: Windows 10/11 (x64), .NET 10 Desktop Runtime, Apple iTunes (for Apple Mobile Device Service), jailbroken device with AppSync Unified

### Package Contents
- `Legacy-IPA-Manager-Windows.zip` — Unified English / Simplified Chinese build including `IdeviceGui.exe`, `IdeviceIndex.exe`, `AfcHelper.exe`, `IdeviceIconHelper.exe`, bundled `idevice` + `aria2c` toolkit, license texts, User Guides, and `SHA256SUMS.txt`.
- Note: No IPA files or `index.db` are bundled with the release.

### Notes
- Unofficial third-party tool. Not affiliated with Apple Inc.
- Use only with software you are legally allowed to use.
