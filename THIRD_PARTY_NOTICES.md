# Third-Party Notices

This repository contains third-party source under `third_party/` and the
corresponding Windows x64 runtime binaries under `toolkit/`.

## Binaries included in this repository

The `toolkit/` directory contains the runtime executables and DLLs used as
separate processes or dynamically loaded libraries by the application. Each
component keeps its upstream license text in `toolkit/LICENSES/` (or beside
`aria2c.exe` in `toolkit/`), and is **not** subject to the PolyForm
Noncommercial restriction.

## Source included in this repository

| Component | License | Location |
|---|---|---|
| libimobiledevice | LGPL-2.1 | `third_party/libimobiledevice/` |
| libimobiledevice-glue | LGPL-2.1 | `third_party/libimobiledevice-glue/` |
| libplist | GPL-2.0 / LGPL-2.1 | `third_party/libplist/` |
| libtatsu | LGPL-2.1 | `third_party/libtatsu/` |
| libusbmuxd | LGPL-2.1 | `third_party/libusbmuxd/` |
| ideviceinstaller | GPL-2.0 | `third_party/ideviceinstaller/` |

The snapshots are exported from upstream source trees. Upstream `.github/`
directories and any machine-specific notes were removed. See
`third_party/README.md`.

## Runtime tools and libraries in `toolkit/`

| Component | License | License text |
|---|---|---|
| aria2 (`aria2c.exe` 1.37.0) | GPL-2.0 | `toolkit/aria2-LICENSE.txt`, `toolkit/aria2-SOURCE.txt` |
| OpenSSL (`libssl`, `libcrypto`) | Apache-2.0 / OpenSSL | `toolkit/LICENSES/openssl-LICENSE`, `toolkit/aria2-LICENSE-OpenSSL.txt` |
| libzip | BSD-3-Clause | `toolkit/LICENSES/libzip-LICENSE` |
| bzip2 | bzip2 license | `toolkit/LICENSES/bzip2-LICENSE` |
| xz/liblzma | Public Domain / 0BSD / LGPL-2.1 / GPL | `toolkit/LICENSES/xz-*` |
| zstd | BSD-3-Clause | `toolkit/LICENSES/zstd-LICENSE` |
| zlib | zlib license | `toolkit/LICENSES/zlib-LICENSE` |

The toolkit is a collection of separate executables and dynamically loaded
libraries. Each component keeps its own upstream license.

## LGPL-2.1 dynamic linking compatibility (`AfcHelper`)

`IdeviceGui/AfcHelper/` is a thin helper executable that dynamically loads
`libimobiledevice-1.0.dll` and `libplist-2.0.dll` (LGPL-2.1). In accordance
with LGPL-2.1 Section 6, users are permitted to replace or relink compatible
versions of those LGPL-2.1 libraries and to debug/reverse-engineer `AfcHelper`
solely for that purpose. `IdeviceGui` and `IdeviceIndex` communicate with
`ideviceinstaller.exe`, `aria2c.exe`, and `AfcHelper.exe` strictly as separate
subprocesses.

## Build-time dependencies

| Component | License |
|---|---|
| .NET / WPF | MIT |
| Microsoft.Data.Sqlite | MIT |
| xUnit v3 (tests) | Apache-2.0 |

See the package metadata for the exact license text of each NuGet package.

## First-party source

Starting with **v1.1.0**, the first-party GUI source and original assets in
this repository are licensed under the **PolyForm Noncommercial License 1.0.0**
(`PolyForm-Noncommercial-1.0.0`). See [LICENSE](LICENSE) and [COPYRIGHT](COPYRIGHT).
Prior releases (`v1.0.0` and `v1.0.1`) remain under `GPL-3.0-only`.
