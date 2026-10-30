# Changelog

FlixyPix follows [semantic versioning](https://semver.org/). Before 1.0, a minor version
(0.x) may change formats and behavior; the release notes say when.

## Unreleased

- Fixed mouse-wheel routing through nested folder views, including partial wheel steps and outer-sidebar handoff.
- Grid previews use 16:9 frames and fit the full image by default; the library remembers an optional fill/crop mode, also used by preview clips.
- Added contextual keyboard navigation, F6 region switching, keyboard-adjustable sliders and navigation/selection beyond loaded media windows.
- Added an application menu (F10), searchable offline help (F1), current shortcut reference (Ctrl+F1), version, bundled release/license text and local diagnostics.

- Sidebar sections now use themed cards with integrated header actions. Folder navigation has its own resizable viewport, two-axis scrolling, folder icons and a compact subfolder switch; height and offsets are remembered per library.
- High-resolution home artwork is now restricted to the visible featured video banner. Ordinary cards use standard thumbnails; hidden or replaced banner requests are cancelled.

- Browse registered media folders as separate roots, with a remembered subfolder switch and collapsible sidebar sections per library.
- Quick tags use fixed, editable shortcuts with separate badges and support Ctrl plus number-pad digits.
- Filter unrated media and missing or unreadable thumbnails; sorting now clearly labels recently added items.
- Media cards offer a direct quick-collection toggle, including on the home page.
- Home banners and visible cards load larger local artwork on demand, with a bounded cache and cancellation on navigation.
- Catalog schema 17 adds thumbnail-health state; existing libraries receive the usual migration backup.

- Jump mark navigation now wraps in both directions across buttons, shortcuts and assigned mouse buttons.
- Click the volume track to choose a level directly; the volume button toggles mute and changes its symbol.
- Rename marks in the player with the pencil button, F2 or the row context menu. Editing keeps its position
  during playback and preview refreshes, and updates the library list without overwriting other details.


- Fixed mouse clicks and wheel input over embedded video on Windows, including double-click full screen,
  configured mouse buttons, modifier keys and partial wheel steps.
- Player shortcut settings now show when assignments are inactive; the enable switch is beside the assignments.
- The jump mark panel remembers its visibility per library across videos and restarts. Full screen temporarily
  hides it and restores its previous state on return.

## 0.5.0-preview

First public preview.

- Optional Adult Content Pack: 62 genres in ten languages, alias-aware search and filename
  suggestions, repeat-safe import and undo until the import dialog closes. Performers, studios
  and the untranslated genre names are read from an installed EazyFlixPix 4; FlixyPix does not
  ship these lists.
- Local media library for videos, images and audio with tags, ratings, collections, saved
  searches, a directory, an advanced search, an mpv-based player and an image viewer.
- Database management: several libraries, check and repair, scheduled backups, import and export,
  optional catalog encryption.
- Import of EazyFlixPix 4 catalogs. Microsoft SQL Server Compact is no longer bundled; the
  import uses an existing EazyFlixPix installation or a system-wide SQL Server Compact 4.0 SP1.
- Ten interface languages, with English texts as translation keys.
- Player: a click on the time line jumps there; the mouse wheel seeks by default (Ctrl+wheel changes the volume,
  also over the control bar), libraries with the old volume default switch once; a double click only toggles full
  screen; middle click and M mute; the jump mark list hides with its own button or L.
- A damaged or hand-edited appsettings.json no longer prevents the start: readable values are kept,
  the rest falls back to defaults or the last working copy; the damaged file is kept beside it.
- Catalogs of other EazyFlixPix 4 versions import as far as they can be read; the result names
  areas that were partial or absent.
- "Add to collection" offers a new playlist or slideshow right away and adds the whole selection.
- License changed to GPL-3.0-or-later; the EazyFlixPix catalog reader is also available under
  the MIT License.
