# Third-party notices

FlixyPix is licensed under the GNU General Public License, version 3 or (at your option) any
later version (`LICENSE`). The SDF exporter in `src/FlixyPix.LegacyExporter` is additionally
available under the MIT License (`src/FlixyPix.LegacyExporter/LICENSE`).

FlixyPix uses the components below. Each keeps its own license. The portable package contains
their license files under `tools/notices`; the build collects the NuGet notices into
`tools/notices/dependencies` (`scripts/release/collect-notices.py`).

## Libraries (NuGet)

| Component | License | Project |
|---|---|---|
| .NET runtime and Windows Desktop runtime | MIT | https://dot.net/ |
| Microsoft.Data.Sqlite.Core | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw.core | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite3 Multiple Ciphers (SQLite3MC.PCLRaw.*) | MIT | https://utelle.github.io/SQLite3MultipleCiphers/ |
| SharpCompress | MIT | https://github.com/adamhathcock/sharpcompress |
| Magick.NET (with ImageMagick) | Apache-2.0; ImageMagick License | https://github.com/dlemstra/Magick.NET |
| System.Formats.Nrbf | MIT | https://github.com/dotnet/runtime |

The exact versions are pinned in the `packages.lock.json` files.

## Bundled programs

The portable package contains unmodified binaries of the following programs. They run as
separate processes. Their versions and SHA-256 hashes are pinned in
`docs/release/media-tools-lock.json`.

| Program | License | Binary source | Source code |
|---|---|---|---|
| FFmpeg and ffprobe (essentials build by Gyan Doshi) | GPL-3.0 (build configured with `--enable-gpl --enable-version3`) | https://www.gyan.dev/ffmpeg/builds/ | https://ffmpeg.org/download.html; the build configuration and the versions of all included libraries are listed in `tools/notices/FFmpeg-build.txt` |
| mpv 0.41.0 with its libraries | GPL-2.0-or-later (this build) | https://github.com/mpv-player/mpv/releases/tag/v0.41.0 | https://github.com/mpv-player/mpv/tree/v0.41.0 |

## Not bundled

FlixyPix does not ship Microsoft SQL Server Compact. To import an EazyFlixPix catalog, the
exporter uses the copy in an existing EazyFlixPix installation or a system-wide installation of
Microsoft SQL Server Compact 4.0 SP1, which Microsoft offers for download.

FlixyPix is an independent project. It is not affiliated with or endorsed by the authors of
EazyFlixPix.
