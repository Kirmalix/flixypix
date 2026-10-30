# FlixyPix 0.5.0 — Windows x64

This portable release supports Windows 10/11 (64-bit) and has no installer or code signature. Windows SmartScreen may ask for confirmation on first start. Back up the catalog and media files before updating. Version 0.5.0 uses catalog schema 17; an upgrade creates a migration backup. Older application versions may not be able to open an upgraded catalog.

Run `FlixyPix.exe` to open your local media library. The .NET runtime is included. Keep the `tools` folder, containing FFmpeg, ffprobe and mpv, beside the application.

On the first normal start, FlixyPix detects settings from previous releases and copies an earlier default library. Previous data is preserved. A custom library folder continues to be used.

## Language

FlixyPix follows the Windows display language. Choose another language under **Settings → Appearance → Language**, save, then restart. German, English, French, Spanish, Italian, Brazilian Portuguese, Dutch, Polish, Japanese and Russian are available. Numbers and dates follow your Windows regional format independently of the interface language.

The translations were produced and reviewed with AI assistance, not by native speakers; corrections are welcome. Individual translation overrides can be placed in `%LOCALAPPDATA%\FlixyPix\languages\<code>.json`; see the localization documentation in the source project.

## Getting started

Import files or folders, or drag them onto the window. Source media stays in its original location. The SQLite catalog stores metadata, tags, ratings, descriptions, categories, collections and saved searches.

- **Media folders:** Add a media folder from the Add menu. FlixyPix compares the folder with the catalog, offers new files, reassigns moved or renamed files and reports missing files. Watched folders show a notification; F5 checks immediately.
- **Organize:** Use advanced search, the tag vocabulary, folder and tag favorites, filename parsers, tagging rules and duplicate detection.
- **Catalog:** Migrate to a new library, compare snapshots, import/export JSON or CSV, read ZIP/RAR image archives and create complete backups.
- **Jobs:** Queue conversions, clips, GIFs, crops, joined videos and scene images. File management offers batch actions for items selected with Ctrl or Shift.

The player supports pause, seeking, volume, speed, audio/subtitle tracks, fullscreen and time-dependent views. Edit and reorder scenes and bookmarks and attach saved artwork. Image windows support zoom, rotation, page/frame navigation and pausing animations.

## Starter tag packages

`tag-vocabularies` contains one `.flixytags.csv` file per interface language, each with the same 62 adult genre concepts. With an installed EazyFlixPix 4, the Adult Content Pack dialog also offers its performer and studio lists and the original genre names (English (original)); FlixyPix reads them from that installation and does not ship them. Choose the package you want in the directory's tag-package import, inspect the preview and select the rows to apply.

Packages add vocabulary and category roles; they do not tag media automatically. Switching the interface language does not rename existing tags or categories. The CSV header and technical role values remain stable across languages.

## Data, backup and shortcuts

Default catalog: `%LOCALAPPDATA%\FlixyPix\Library`.
Computer settings: `%LOCALAPPDATA%\FlixyPix\appsettings.json`.
A change of library folder takes effect after restart. Older catalogs receive a consistent backup before a schema upgrade.

JSON format 4 includes the catalog, saved artwork, historical drafts and preserved binary data in checked sections. Versions 1–3 remain readable. External media is included in a complete `.flixypixbackup` only when explicitly selected. Restore verifies checksums and requires a new destination folder; it does not replace the current catalog.

An offline media drive does not prevent catalog migration. Paths are preserved without preparing media automatically. Historical searches, parsers and job drafts remain inactive until explicitly adopted. Tag artwork is managed under Organize.

Menu → Help (F1) opens the offline help; Ctrl+F1 shows current keyboard shortcuts. Menu → About FlixyPix shows the version.

Ctrl+O imports, Ctrl+F searches, Ctrl+S saves details and F5 checks media folders. Enter opens an item. Delete removes the catalog entry; permanently deleting a file is a separate, confirmed action.

## Compatibility and licenses

This is newly written software. Full behavioral parity with EazyFlixPix 4.8.4 has not been established. The original application is not started. The Add menu can take over an EazyFlixPix 4.8.4 library using a bundled tool operating on a database copy. This needs Microsoft SQL Server Compact 4.0, which FlixyPix does not ship: either an existing EazyFlixPix installation or Microsoft's SQL Server Compact 4.0 SP1 (x86) package. Optional library encryption uses your own password. Online providers and live TV are not available.

Catalog → Legacy data / Migration → Historical functions offers explicit adoption of supported search, parser and job settings after preview. Unknown active options remain blocked. Adopted jobs start only when separately started in the queue. Legacy tag artwork can be copied after reviewing its mapping.

FlixyPix is free software under the GNU General Public License, version 3 or later; see `LICENSE`. The tool in `legacy-import` is also available under the MIT License. Third-party components keep their own licenses: overview in `THIRD-PARTY-NOTICES.md`, texts under `tools/notices`. Source code: https://github.com/Kirmalix/flixypix. FlixyPix is an independent project, not affiliated with the authors of EazyFlixPix. The German portable instructions are in `README.de.md`.
