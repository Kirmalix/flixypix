# Changelog

FlixyPix follows [semantic versioning](https://semver.org/). Before 1.0, a minor version
(0.x) may change formats and behavior; the release notes say when.

## 0.7.0

**A calmer start**

- The window stands at once and shows its outline while the library opens, with the logo for a moment above it.
  The change to the finished window is a soft fade. Cancelling is offered when a start takes longer than usual.
- An encrypted library asks for its password first; the logo follows the answer. With a neutral title the start
  shows neither logo nor name.
- The welcome page of an empty library invites you to drop files, and during the first import the new items appear
  while they are found.
- The window takes its place before it appears instead of moving during the start, and always fits the screen.
  It may now be smaller (minimum height 560), so it fits laptops with a large display scale.
- FlixyPix is sharp on every monitor, also with different scaling, and the title bar follows the colour scheme on
  Windows 11. The package starts from precompiled code, which makes starts a little faster.

**A window that moves with you**

- Buttons, navigation entries and lists answer the pointer softly, and a pressed button gives way a little.
- Areas settle into place when you change them, the tiles of the next page come from the side you are heading to,
  and new filter results arrive softly. The side bars and details glide open and shut. Nothing waits for a motion:
  the new content shows as soon as it is ready. All of this stops when Windows turns animations off.
- Notices rise into place and leave to the side; the arrows of folding groups turn, and a new favorite answers
  with a short pop.

**A more consistent look**

- Questions answer with what will happen, such as "Delete" or "Discard", instead of Yes and No.
- Selected entries look the same everywhere, and corners and text sizes follow fixed steps.
- A notice now says what happened, such as which item became a favorite, and the status line no longer repeats it.
- "Undo" and other coloured text are easier to read on dark surfaces.
- With a contrast theme of Windows, FlixyPix uses its colours, and follows when the theme changes.

**Lock**

- A PIN is now also asked for at program start, before libraries can be seen; this can be switched off under
  Settings → Privacy → Lock. An encrypted library that opens directly asks only for its password. Like the lock,
  this guards the screen; only the encryption protects the files.
- While a PIN is set, the boss key also locks: nothing appears on screen, and showing FlixyPix again asks for the
  PIN. **Upgrading:** this is on by default; switch it off under Settings → Privacy → Lock.

**Fixes**

- FlixyPix can run from a drive named U:. That drive letter was always treated as write-protected, so a
  portable copy on it refused to start and files on it were always listed as offline. **Upgrading:** U: is
  no longer protected by itself; choose folders FlixyPix must never write to under Settings → Storage →
  Protected folders. Folders holding FlixyPix's own data or a library cannot be protected.
- The dialog for assigning media paths no longer suggests example folders.
- The time line follows the pointer when dragged from anywhere on it, not only from its handle, and answers at once
  also in long videos.
- The video area stays black until the picture starts, so no second control bar shows there for a moment.
- A late answer from the video player no longer reads "Playback finished" and stops the time display while the
  video plays on. The time display keeps following, and a player silent for longer is reported as not responding.
- A larger interface size keeps the arrangement of the player. The control row stays one line with playback
  on the left and the tools on the right; what does not fit moves into a "More actions" menu. The player and
  dialogs open larger to match, as far as the screen allows, while windows already open keep their size
  unless they fall below the larger minimum size.
- Menus, tooltips, the full-screen bar, the time line preview and the play symbol follow the interface size.
  At 85 and 90 % the full-screen bar no longer runs off the screen.
- The main window folds its side bars according to the interface size, so the search and the buttons at the
  top stay in the window; colour scheme tiles and the action buttons of the detail page wrap when space is short.

**Player**

- A calmer player bar: only the time line and one row of controls remain, so the video gets the room. Play
  stands out in the middle, with the times at the ends of the time line. When the window narrows, buttons keep
  their size: the rating shrinks to one star, then tools move into the "More actions" menu, and a narrow
  window hides the jump mark list unless you open it.
  The file path moved to the tooltip of the time and into the right-click menu, and messages appear briefly
  over the video instead of in a status line.
- One range replaces the separate A–B loop and scene buttons: set a start and an end, then save it as a scene
  or loop it. **Changed:** a loop now starts with a click on "Loop" and no longer by itself after the end is
  set. The range tools stand in the jump mark list; while the list is hidden or in full screen, a small row
  appears once a range is begun, and the range shows on the time line.
- The pencil in the jump mark list appears when the pointer is over a mark, which leaves more room for names.

## 0.6.0

**Upgrading from 0.5.0:** the catalog format changes. FlixyPix asks before converting a library and
keeps a copy of the catalog beside it; 0.5.0 cannot open a converted library. Back up your media files
separately. Ctrl+1–9 no longer sets tags: pinned tags now work with 1–9 in the tag mode (T).

**A new layout**

- The left bar holds the areas and your collections, and folds to symbols. Tags and common filters open
  in a panel beside the media; every other filter is still one click away. Active filters stand as
  chips in the search field.
- Folders are an area of their own: walk through them with the tree, the path above the media or the
  subfolders. "Show in folder" in the details goes straight there.
- Normal work stays in the main window. The scene wall and media jobs are pages, and a double click
  opens a page per medium with its picture, timeline, marks, history and similar media. Enter and the
  play button on a tile still play.
- Performers and studios are galleries, most frequent first, with a page per entry; the table for
  maintaining entries is one click away.
- "Organize" is a work area: queues such as without tags and unrated are worked through one item at a
  time with visible progress, can be narrowed to videos, images or audio, and stand beside the
  maintenance tools.
- The start page leads with what is left to do, continue watching, recently added and recently tagged,
  instead of a banner and a notice at start.
- The details appear only while something is selected. A bar offers tag mode, collections and more for
  several items, and says when some are out of view.
- Interface size from 85 to 160 % (settings, Ctrl+Plus, Ctrl+Minus, Ctrl+0), a calmer look with the
  accent only on the main action and the current choice, and one type scale throughout.

**Tagging**

- Tag mode (T): tag with one hand using 1–9 and Q W E R, move on with the arrow keys.
- Suggestions say why they appear, compare against how common a tag is, send performers and studios to
  their categories and fall behind when ignored. Applied tags come first, and only their × removes them.

**New**

- Import from Stash: a wizard for scenes, images, galleries, groups, performers, studios and tag parents,
  with images inside archives, fast also for large exports.
- Organize: review inbox, selective parser apply, image sets, publication groups, tag hierarchy and a
  duplicate hub.
- Player: A–B loops, playback preferences per video, seek previews from memory and times on contact
  sheets; a compact bar with loop, bookmark and scene marking, grouped jump marks and a star rating.
- Portable storage with safe switching between portable and profile mode.
- Folder synchronization and undo of immediate edits per field.

**Better**

- Lists to work through: what an edit moves out of a queue stays visible as done until "Update list" or
  another view. "Create thumbnails for this list" handles all its videos.
- The thumbnail filter answers at once. The check behind it skips images, which need no thumbnail, and
  no longer decodes common formats, so it stays quick in large libraries; a file cut off further on
  counts as readable.
- Boss key: system-wide by default, also with Esc; the program in front still receives the key. Hidden
  windows return with a separate shortcut.
- Moving a library rewrites only media whose paths change, and libraries taken over before their location
  was recorded find their thumbnails again.

Compatibility and distribution:

- Catalog schema 26. Libraries from 0.5.0 (schema 17) convert on opening; older ones must first be opened
  with 0.5.0.
- The ZIP includes the .NET runtime, mpv, FFmpeg, ffprobe and third-party notices. There is no installer
  or code signature yet; Windows SmartScreen may ask for confirmation.
- Translations are AI-assisted and have not received native-speaker review.

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
