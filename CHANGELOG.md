# Changelog

FlixyPix follows [semantic versioning](https://semver.org/). Before 1.0, a minor version
(0.x) may change formats and behavior; the release notes say when.

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
