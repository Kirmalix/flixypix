# Changelog

FlixyPix follows [semantic versioning](https://semver.org/). Before 1.0, a minor version
(0.x) may change formats and behavior; the release notes say when.

## Unreleased

## 0.5.0

First public release for Windows 10/11 (64-bit), distributed as a portable ZIP.

- Catalog videos, images and audio where they are, with tags, ratings, descriptions,
  collections, saved searches, advanced search, filename rules and duplicate detection.
- Browse media folders as separate roots in a resizable, scrollable sidebar. Collapsible
  sections, folder scope, tree expansion and display settings are remembered per library.
- Assign fixed quick tags with Ctrl+1–9 or number-pad digits; add items to the quick
  collection directly from media cards. Filter unrated media and missing or unreadable
  thumbnails, and sort by recently added items.
- Display complete thumbnails in 16:9 grid frames, with an optional remembered crop mode.
  High-resolution home artwork is generated locally only for the visible featured video.
- Play video and audio through mpv. Native video clicks and wheel input work with configured
  buttons and modifiers, including double-click full screen and partial wheel steps.
- Navigate jump marks in both directions with wraparound. Rename marks with the pencil,
  F2 or context menu. The jump mark panel remembers its visibility across restarts.
- Set volume directly by clicking the track; the volume button toggles mute and changes
  its symbol. Player settings clearly identify inactive shortcuts.
- Navigate by keyboard, including F6 region switching, focused sliders, context menus and
  selection across loaded media windows. The application menu provides offline help (F1),
  current shortcuts (Ctrl+F1), version, licenses and local diagnostics.
- View images with zoom, rotation, slideshows and ZIP/RAR image archives. Create thumbnails,
  clips, GIFs, joined videos and conversions through the FFmpeg job queue.
- Manage multiple libraries, optional catalog encryption, check/repair, scheduled backups,
  and import/export through JSON, CSV, NFO, M3U and tag packages. Damaged settings can recover
  from readable values or a last working copy.
- Import EazyFlixPix 4 catalogs into a new library without changing the original. Reading
  them requires an existing EazyFlixPix installation or Microsoft SQL Server Compact 4.0
  SP1 (x86); SQL Server Compact is not bundled. Partial or absent import areas are reported.
- Optional Adult Content Pack: 62 genres in ten languages, alias-aware search and filename
  suggestions, repeat-safe import and undo while the import dialog is open. Performer,
  studio and original genre lists are read from an installed EazyFlixPix 4, not shipped.
- Ten interface languages. Translations are AI-assisted and have not received native-speaker
  review. FlixyPix is GPL-3.0-or-later; the catalog reader is also available under MIT.

Compatibility and distribution:

- Catalog schema 17 stores thumbnail-health state. Existing catalogs receive an automatic
  migration backup; older application versions may not open an upgraded catalog. Back up
  media files separately, or explicitly include them in a complete backup.
- The ZIP includes the .NET runtime, mpv, FFmpeg, ffprobe and third-party notices. Keep the
  bundled folders beside FlixyPix.exe. There is no installer or code signature yet;
  Windows SmartScreen may ask for confirmation.
- Full behavioral parity with EazyFlixPix 4.8.4 has not been established. Online providers
  and live TV are outside FlixyPix's scope.
