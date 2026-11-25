# Third-Party Notices

This repository contains third-party source under `third_party/` and the
corresponding Windows x64 runtime binaries under `toolkit/`.

## Binaries included in this repository

The `toolkit/` directory contains the runtime executables and DLLs used by the
application. Each component keeps its upstream license text in
`toolkit/LICENSES/` (or beside `aria2c.exe` in `toolkit/`).

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
libraries. It is not relicensed as GPL-3.0 as a whole; each component keeps
its own license.

## Build-time dependencies

| Component | License |
|---|---|
| .NET / WPF | MIT |
| Microsoft.Data.Sqlite | MIT |
| xUnit v3 (tests) | Apache-2.0 |

See the package metadata for the exact license text of each NuGet package.

## Our source

The GUI source in this repository is licensed under GPL-3.0-only. See
[LICENSE](LICENSE) and [COPYRIGHT](COPYRIGHT).
