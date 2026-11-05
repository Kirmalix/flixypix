# Changelog

FlixyPix follows [semantic versioning](https://semver.org/). Before 1.0, a minor version
(0.x) may change formats and behavior; the release notes say when.

## Unreleased

The catalog format changes; libraries opened with this version
are upgraded and should not be opened with 0.5.0 afterwards (a copy is kept before the upgrade).

- Tag mode (T): tag with one hand using 1–9 and Q W E R, move on with the arrow keys; rows and keys stay put.
- Suggestions say why they appear, compare against how common a tag is, send performers and studios to their
  categories and fall behind when ignored; applied tags come first and only their × removes them.
- The folder filter shows as a chip, and a bar states selections the grid cannot show.
- Moving a library rewrites only media whose paths change.
- Import from Stash: a wizard for scenes, images, galleries, groups, performers, studios and
  tag parents, with explicit version confirmation, images inside archives and guarded
  transactions, fast also for large exports.
- Organize: review inbox, selective parser apply, image sets, publication groups, tag
  hierarchy, a duplicate hub, performer and studio views and a paged scene wall.
- Player: A–B loops, playback preferences per video, seek previews from memory and times on
  contact sheets. A compact bar with symbol groups for loop, bookmark and scene marking, one
  symbol for audio and subtitle tracks, grouped jump marks and a star rating.
- Sidebar: library entries in foldable groups with icons, scrolling together with the cards.
  The review inbox appears once reviewing is in use (setting: automatic, always, never), audio
  only with audio media. Favorites and Watch later are fixed rows of the collection card; tags
  and tag categories share one card with a list/categories switch and visible sorting.
- Boss key: system-wide by default, also with Esc; the program in front still receives the
  key. Hidden windows return with a separate shortcut.
- Portable storage with safe switching between portable and profile mode. Libraries taken over
  before their location was recorded find their thumbnails again after a move.
- Folder synchronization, media folder tabs and undo of immediate edits per field.
- Thumbnail filter: the list appears at once and missing or damaged thumbnail files join it from
  a check in the background; audio without a picture no longer counts as missing a thumbnail.
- Lists to work through: what an edit moves out of the "without thumbnail" or "unrated" filter
  stays visible as done until "Update list" or another view. "Create thumbnails for this list"
  handles all its videos.
- Folder tree: levels shared by all folders without media of their own are left out; the tooltip
  of "All folders" names them.

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
