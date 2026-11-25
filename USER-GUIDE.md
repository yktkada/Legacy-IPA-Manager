# Legacy IPA Manager · User Guide

> IPA library manager, compatibility analyzer, and independent multi-device installation queue for legacy iOS devices on Windows.
> Covers the current UI, keyboard shortcuts, decision rules, and the bundled `IdeviceIndex` component.

---

## 1. Overview

### 1.1 Identity & Components

| Item | Value |
|---|---|
| Product name | `Legacy IPA Manager for Windows` |
| Core function | Local IPA library management, offline compatibility analysis, multi-device installation/uninstall queues |
| Implementation | C# / WPF / MVVM (.NET 10), wrapping `ideviceinstaller.exe`, `AfcHelper.exe`, and `IdeviceIconHelper.exe` |
| UI language | Bilingual single-source resources (`System` / `Simplified Chinese` / `English`; runtime logs remain Chinese) |
| Companion | `IdeviceIndex.exe` (essential bundled component for offline index browsing, `aria2c` downloading, and named-pipe auto-import) |

An **IPA** is the installation package format of an iOS app. This program scans, parses, and installs such files over USB.

### 1.2 Supported Scope

| Item | Value |
|---|---|
| Devices | Verified: iPhone 4 (iOS 4.1), iPad 2 (6.1.3), iPhone 4S (7.1.1), iPhone 5 (8.1.2), iPod touch 5 (8.4.1) |
| iOS versions | **iOS 4 / 6 / 7 / 8 and iPad verified**; **iOS 9 / 10 unverified** |
| Prerequisite | Target devices must be jailbroken with **AppSync Unified** |
| Concurrency | Independent queue and serial pump per device; 4 devices installing simultaneously verified |
| Library scale | 423 real packages / 97.8 GB verified; main app benchmarked at **50,000 rows**; `IdeviceIndex` verified at **200,000 rows** |

### 1.3 Requirements

| Item | Value |
|---|---|
| OS | Windows 10 / 11, 64-bit |
| Runtime | .NET 10 Desktop Runtime |
| iTunes | Required (provides Apple Mobile Device Service); no Apple ID sign-in needed |
| Cable | USB data connection |
| Directory permissions | Program directory must be writable; if read-only, the program reports and exits at startup without falling back to `%APPDATA%` or `%TEMP%` |

---

## 2. Interface and Operations

### 2.1 Regions

| Region | Content |
|---|---|
| Top bar | `Add...`, `IPA Index...`, `Install to...`, `Hide log`, `Settings`, plus active device details and `More ▾` |
| Device tab strip | Fixed leftmost block shows active device run-state colour; one tab per online device with segmented progress blocks; offline tabs greyed out and moved to the end |
| Left · view switch | `Library` and `Device apps` as two equal-width fixed buttons |
| Left · library toolbar | Multi-select, user group dropdown, `Manage groups`, `Incremental scan`, `Full rescan`, compatibility filter, advanced filter, merge by BundleId |
| Left · quick jump bar | Above the grid: horizontal `0–9`, `A–Z`, `#` buttons (supports Chinese Pinyin initials), shared by `Library` and `Device apps` |
| Left · grid | Virtualized Library rows or Device apps rows; supports `Shift + Mouse Wheel` horizontal scrolling with a 2–3 px slim scrollbar |
| Right · queue | Queue and progress of the current device tab; defaults to Status / Note / Name / Version / Installed size |
| Bottom right · log | Operation log; verbose/concise toggle, export, clear; collapsible via button or `Ctrl+L` |

### 2.2 Top Bar

| Control | Function |
|---|---|
| `Add...` | Submenu: `Add files...` imports selected `.ipa` files; `Add folder...` adds a recursive scan root and triggers a scan |
| `IPA Index...` | Opens the bundled `IdeviceIndex.exe` browser/downloader |
| `Install to...` | Queues selected packages to all online devices, a picked device, or via the multi-device selection dialog |
| `Hide log` | Collapses or expands the log pane (`Ctrl+L`) |
| `Settings` | Opens the settings window (8 setting tabs + About) |
| Device info & `More ▾` | Shows `Name · Model · iOS(Build) · Free/Total GB · Color · Battery Health%` (+ ECID when wide enough); `More ▾` shows battery telemetry and copies ECID / Serial / UDID / ModelNumber / HardwareModel |

### 2.3 Devices and Queue

| Control | Function |
|---|---|
| `Refresh` | Forces a full re-probe of all connected devices; normal plug/unplug detection is driven by Windows USB events with low-frequency fallback, and automatic incremental refreshes **only probe newly added or recovered devices without disturbing active installs** |
| `Suspend` / `Resume` | One button matching device run state: `Suspend` when Idle/Running; `Resume` when Paused/Suspended/Offline |
| `Cancel` | Cancels the selected job. During transfer it kills the process tree cleanly; once installing begins, it waits for completion and uninstalls automatically |
| `Clear` | Removes finished jobs and clears red/yellow/green marks in the library |

Default queue row layout:

```text
[status progress] | note | app name | version | installed size
```

- Notes only appear on Failed / Paused / Suspended jobs.
- When switching between device tabs, the main window saves and restores the **Library grid's vertical scroll offset** per device `UDID` in memory (queue and log scroll positions are not tracked per tab).
- `Device apps` supports independent multi-select and batch uninstall.

### 2.4 Library Toolbar

| Control | Function |
|---|---|
| `Multi-select: off` / `Multi-select: N selected` | Toggles multi-select mode and shows selection count |
| Group dropdown | Switches between `All` and user groups (empty groups greyed out) |
| `Manage groups` | Creates, renames, and deletes user groups |
| `Incremental scan` | Reconciles new, changed, and missing files |
| `Full rescan` | Re-parses every present file cleanly |
| Compatibility dropdown | `Off` / `Installable` / `Adapted` (rules in 4.1) |
| Advanced filter switch + `Advanced filter (N)` | Group-internal AND/OR, cross-group AND; filters by version range, size range, architecture, origin, state, compatibility |
| `Merge packages with the same BundleID` | Collapses matching BundleIds into group rows with version range and count |

### 2.5 Quick Jump Bar & Grid

| Item | Content |
|---|---|
| Quick jump bar | Horizontal **`0–9` / `A–Z` / `#`** bar above the grid supporting English letters, digits, **Chinese Pinyin initials**, and `#` for symbols; adapts on narrow windows in 3 stages: hide unavailable grey buttons → tighten spacing → 2–3 px slim horizontal scrollbar (`Shift + Wheel`) |
| Columns | Check (`✓`) · Icon · Name · Uncompressed size · Version · Minimum iOS · Device family · Origin · BundleId · Architecture · Original size · Status · File path |
| Column operations | Drag headers to reorder; drag edges to resize; toggle visibility under `Settings → Library` (all persisted) |
| Row height | Small (24 DIP) / Medium (32 DIP) / Large (40 DIP) |
| Group row | Version range + `N` count; collapsed by default; crisp integer `1` DIP borders |
| Check column | Shows installed status on the active device (rules in 4.2) |
| Missing rows | Rows whose file is absent on disk turn grey (`IsMissing`); hide via `H` or clean records + rescan via `D` |

### 2.6 Log Pane

| Control | Function |
|---|---|
| `Switch to detailed view` / `Switch to compact view` | Toggles log detail level |
| `Export…` | Saves visible log lines to text |
| `Clear` | Clears only the UI log pane without touching `logs\app.log` |

### 2.7 Mouse Gestures

| Gesture | Effect |
|---|---|
| Click a column header | Sorts ascending/descending |
| Click a group row | Expands or collapses that group |
| Double-click a file row (≤ 400 ms) | Arms blue and enqueues or removes from the current device queue |
| Drag in multi-select mode | Toggles checked state across dragged rows |
| `Shift + Mouse Wheel` | Horizontal scrolling in grids and the quick jump bar (2–3 px slim scrollbar) |
| Right-click a data row | Selects that row and opens the context menu (empty grid area clears menu) |

### 2.8 Context Menu & Keyboard Shortcuts

Context menu items render their single-letter mnemonic inside the **left icon gutter**:
- **With menu open**: press the single letter directly;
- **Without opening the menu**: press **`Ctrl + Alt + <Letter>`** with focus in the grid (`Ctrl + L` globally toggles the log pane).

**Main App — Library Single-Row / General:**

| Key | Menu / Action | Notes |
|---|---|---|
| `I` | Force install to current device | Skips minimum iOS and free-space checks (single valid IPA only) |
| `C` | Copy (submenu) | Submenu keys: `B` BundleId, `N` Name, `V` Version, `O` Min iOS, `F` File name, `P` File path, `R` Entire row (Tab-separated) |
| `E` | Open file location | Selects the IPA file in Windows Explorer |
| `G` | Add to group | Opens quick group selector |
| `W` | New group... | Creates group and adds item |
| `M` | Manage groups... | Renames/deletes groups |
| `X` | Remove from current group | Enabled inside a user group |
| `H` | Show / Hide missing files | Toggles grey missing-file rows |
| `D` | Delete missing records | Removes dead DB records immediately (never deletes disk files) and automatically runs an incremental rescan |

**Main App — Multi-Select & Device Apps:**

| Scope | Key | Action |
|---|---|---|
| Library (Multi-select) | `A` / `K` | `A` enqueue to all online devices; `K` open `Install to...` dialog |
| Library (Multi-select) | `S` / `Z` | `S` select all visible rows; `Z` clear selection |
| Device Apps | `U` / `T` / `D` | `U` uninstall focused app; `T` toggle multi-select; `D` batch uninstall checked apps |
| Device Apps (Multi-select) | `S` / `Z` | `S` select all; `Z` clear selection |

### 2.9 Closing Progress Status

When closing either `IdeviceGui` or `IdeviceIndex`, if shutdown tasks are still finishing (saving layout, closing import pipe, waiting for scan/DB load, stopping device queues or `aria2c`, waiting for icon decoding, saving settings/groups, or killing lingering child processes), the window title appends **` - Closing - <current step>`** (Chinese UI: **` - 正在关闭 - <当前步骤>`**).

---

## 3. Working Logic

### 3.1 Three Stages

| Stage | Input | Output | Persistence |
|---|---|---|---|
| Scan | `.ipa` under scan roots | Parsed rows | SQLite: `data\library.db` |
| Library | Parsed rows | Filtered, sorted, grouped view | Memory only; groups in `data\groups.json` |
| Queue | Library rows + device | Install / uninstall results | Memory only; cleared on exit |

### 3.2 Two-Level State Machine

**Device queue run states**: `Idle`, `Running`, `Paused` (device-level stop), `Suspended` (queue-level stop), `Offline`.

**Task states**: `Waiting`, `Verifying device` (`idevice_id` + `ProductType`/`ProductVersion`), `Checking`, `Cleaning` (`/PublicStaging` via `AfcHelper`), `Checking space`, `Installing` / `Uninstalling`, `Reading back` (`list --xml`), `Completed`, `Failed`, `Paused`, `Suspended`, `Cancelled`.

### 3.3 Install & Uninstall Sequences

- **Install**: `Waiting → Verifying device → Checking → Cleaning → Checking space → Installing → Reading back → Completed`
  - Cleaning failure never blocks installs unless AFC is unreachable (`Paused`). Insufficient space after cleaning suspends the queue safely (`Suspended`).
- **Uninstall**: `Waiting → Verifying device → Checking → Uninstalling → Reading back → Completed`
  - Skips compatibility, staging cleanup, and space checks; relies on `list --xml` readback because iOS 6/7 return exit code `0` even when uninstalling an absent app.

### 3.4 Timeouts & Concurrency

| Call | Timeout | Outcome |
|---|---|---|
| `idevice_id -l` | 5 s | Device marked offline |
| `ideviceinfo` probe | 15 s | Device marked unresponsive |
| AFC helper | 30 s total / 15 s no output | Process tree killed; queue paused |
| `ideviceinstaller install` | 180 s without output | Process tree killed, device re-probed |
| `ideviceinstaller list --xml` | 30 s | Treated as failed |

Each device has one dedicated serial pump and one memory-only lock (zero IO or child processes inside locks). Failures on one device never affect other devices.

---

## 4. Decision Rules

### 4.1 Compatibility

- **Off**: no filter.
- **Installable**: package device family permits the device class and `MinimumOSVersion` ≤ device OS.
- **Adapted**: passes `Installable` and highest internal screen tier (`UILaunchImages` / storyboards / icons) ≥ device screen tier (`HardwareModel` primary, `ProductType` fallback). Packages with missing/`unknown` screen evidence default to earliest OG 3.5" iPhone tier.

### 4.2 Installed Markers (`✓` Column)

| Case | Display |
|---|---|
| Same BundleId & exact same version installed | **Green background + ✓** |
| Same BundleId & different/unknown version installed | **Light blue (`#D6E9FF`), no ✓** |
| Not installed | White background, no ✓ |
| Group row | Any member exact match → **Green + ✓**; else any alternate version → **Light blue** |

### 4.3 Queue Ordering & Space Accounting

- **Order**: `1. Uninstalls first → 2. Same kind by PeakBytes (original + uncompressed) descending → 3. FIFO`.
- **Space**: `Required = PeakBytes + 10 MB margin` (when uncompressed size is unknown, `compressed × 2`); checked live after `/PublicStaging` cleanup.

### 4.4 Success Verification & Automatic Behavior

- **Success**: exit code `== 0`, no `ERROR` line in `stderr`, and up to 3 `list --xml` readbacks confirm presence/absence.
- **Auto-retry**: up to configurable attempts (default 2, range 2–5) unless a different version is already confirmed installed.
- **Disconnect & Auto-resume**: USB drop marks active task `Paused` and waiting tasks `SuspendedByDisconnect`; reconnect stability window (default 3 s, range 1–10 s) automatically triggers readback recovery and resumes.

---

## 5. Irreversible Operations

| Item | Behavior |
|---|---|
| Overwrite install | Installing over an existing BundleId recreates its container and wipes sandbox data without confirmation |
| Delete missing records | Runs immediately; deletes DB records only, **never disk files**, and triggers an incremental rescan |
| Cancel during install | Waits for install completion, then automatically uninstalls |
| Memory-only queues | Queues and active device lists are not saved across app restarts |

---

## 6. Settings (8 Tabs + About)

1. **Library**: row height (24/32/40 DIP), search debounce threshold/delay, default sort column/direction, match mode (`Contains`/`Prefix`/`Exact`), default search columns, merge same BundleId, show missing files, Retina vs `R` label toggle, per-column visibility switches.
2. **Scan**: scan root list (`RootN`), batch DB write size, **Skip incremental scan on startup** (`SkipStartupScan`).
3. **Icons**: cache limit (default 2048 MB), scale matching, disk downscaling.
4. **Log**: file log `logs\app.log` toggle (off by default; 5 MB × 3 rotation), detailed mode toggle.
5. **Layout (and Language)**: hide log pane, overlay width, reset default 45/27/28 column split, **UI language** (`System` / `Simplified Chinese` / `English`, applies on restart).
6. **Queue**: queue column order & visibility.
7. **Devices**: poll interval (2 s), disconnect stability window (3 s), auto-resume toggle, automatic retry count (2–5), device info field order & visibility.
8. **Security**: **Allow writing outside program directory** (off by default; requires confirming an explicit risk dialog with no pre-checked box).
9. **About**: version & licensing (unaffected by Apply/Cancel/Reset).

---

## 7. Data Locations & Privacy

All state lives inside `<program dir>\data` and `<program dir>\logs` (`library.db`, `groups.json`, `icons/`, `deviceicons/`, `tmp/`, `indexer.json`, `index/`, `app.log`, `crash.log`). Writing to `%TEMP%`, `AppData`, `Windows`, or reparse points outside the program directory is always blocked. The main app makes zero network connections.

---

## 8. Companion Tool: IdeviceIndex

Open via `IPA Index...` or `IdeviceIndex.exe`:
- **Import & Remote Updates**: supports local `index.db` (copy or read-only external link), `manifest.json` / `entries.jsonl(.gz)` / `.json` full rebuilds or incremental `upsert`/`delete` deltas, and `Index.UpdateUrl` remote updates.
- **200,000+ Row Virtual List & Shortcuts**: defaults to full virtualized scrolling (optional 500-row paging in Settings), shared **`0–9 / A–Z / #` Pinyin jump bar**, `Shift + Wheel` horizontal scroll, and `93`-version iOS quick filter (`2.0`–`10.3.4`). Shortcuts (`<Letter>` in menu / `Ctrl+Alt+<Letter>` global): `G` expand/collapse, `Q` enqueue focused, `S` enqueue checked, `C` copy submenu (`N/B/V/O`), `H` row height (`S/M/L`), `Z` clear selection, `Ctrl+L` toggle right pane.
- **Verified `aria2c` & IPC Auto-Import**: validates `<program dir>\aria2c.exe` (1.37.0) via 4-step SHA-256 verification and notifies `IdeviceGui` via the `IdeviceGui.Import.v1` named pipe when downloads finish inside a scan root.

---

## 9. License

- Application code: **GPL-2.0-or-later** ([COPYING](COPYING), [LICENSE](LICENSE)).
- Bundled `aria2c.exe` (1.37.0): **GPL-2.0** (`aria2-LICENSE.txt`, `aria2-SOURCE.txt`).
