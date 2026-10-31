namespace FlixyPix.Core;

public enum MediaKind { Image, Video, Audio }

public sealed record MediaItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Path { get; init; }
    public string Title { get; init; } = "";
    public MediaKind Kind { get; init; }
    public long? FileSize { get; init; }
    public DateTimeOffset? FileModifiedAtUtc { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? DurationSeconds { get; init; }
    public int Rating { get; init; }
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Added date and file date of the original ("addedDate", "filedate"), verbatim as it stored them (local time
    /// without offset); the query table sorts and filters by them.</summary>
    public string? OriginalAddedAt { get; init; }
    public string? OriginalFileDate { get; init; }
    /// <summary>File date (original "filedate"): the earlier of creation and last change, see MediaScanner.FileDate.</summary>
    public DateTimeOffset? FileCreatedAt { get; init; }
    /// <summary>When the medium was released or recorded, as entered or imported (NFO, XMP); the age of performers at recording
    /// counts from it, otherwise from the file date.</summary>
    public DateOnly? ReleaseDate { get; init; }
    public long? PlayCount { get; init; }
    public long? ViewCount { get; init; }
    /// <summary>The O counter: how often the user marked a climax with this medium.</summary>
    public long? OCount { get; init; }
    /// <summary>Playbacks that reached the watched share of the history settings; unset until the first playback since then,
    /// when it starts from the plays counted before (see CatalogRepository.FinishPlayback).</summary>
    public long? WatchCount { get; init; }
    public List<string> Tags { get; init; } = [];
    public bool IsFavorite { get; init; }
    public bool WatchLater { get; init; }
    public string? Category { get; init; }
    public Dictionary<string, List<string>> TagCategories { get; init; } = new();
    public string? ThumbnailPath { get; init; }
    public double PlaybackPositionSeconds { get; init; }
    /// <summary>Subtitle file chosen in the player and the subtitle offset in seconds; applied on the next playback.</summary>
    public string? SubtitlePath { get; init; }
    public double SubtitleDelaySeconds { get; init; }
    /// <summary>When the medium was last played or viewed in FlixyPix.</summary>
    public DateTimeOffset? LastPlayedAt { get; init; }
    public string? Description { get; init; }
    public string? LegacyId { get; init; }
    public Dictionary<string, string?> Extra { get; init; } = new();
    /// <summary>Codecs, frame rate, bit rate, HDR and container from ffprobe; null until they were read.</summary>
    public TechnicalInfo? Technical { get; init; }
    /// <summary>Further versions of the same medium (for example 1080p and 4K); <see cref="Path"/> stays the main file.</summary>
    public List<MediaFile> Files { get; init; } = [];
}

/// <summary>Technical data of a video or audio file as ffprobe reports it.</summary>
public sealed record TechnicalInfo
{
    /// <summary>ffprobe codec names such as "h264", "hevc", "av1".</summary>
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    /// <summary>Frames per second of the video stream.</summary>
    public double? FrameRate { get; init; }
    /// <summary>Overall bit rate in bit/s.</summary>
    public long? BitRate { get; init; }
    /// <summary>The picture uses a high dynamic range transfer (PQ or HLG).</summary>
    public bool Hdr { get; init; }
    /// <summary>ffprobe format names such as "mov,mp4,m4a,3gp,3g2,mj2" or "matroska,webm".</summary>
    public string? Container { get; init; }
}

/// <summary>Another file of a medium, with what was measured for it.</summary>
public sealed record MediaFile
{
    public required string Path { get; init; }
    public long? FileSize { get; init; }
    public DateTimeOffset? FileModifiedAtUtc { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? DurationSeconds { get; init; }
    public TechnicalInfo? Technical { get; init; }
}

/// <summary><see cref="Quick"/> is the one quick collection of a library (key B); it takes every kind of media.</summary>
public enum PlaylistKind { Playlist, Slideshow, Quick }

public sealed record Playlist
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public List<Guid> MediaIds { get; init; } = [];
    public List<PlaylistEntry> Entries { get; init; } = [];
    public Dictionary<string, string?> Extra { get; init; } = new();
    public PlaylistKind Kind { get; init; }
    public double SlideSeconds { get; init; } = 5;
    /// <summary>The collection set it lies in; null at the top level.</summary>
    public Guid? ParentId { get; init; }
    /// <summary>Place within its set; equal places are sorted by name.</summary>
    public int Order { get; init; }
}

/// <summary>A folder for collections, smart collections and further sets in the sidebar; it holds no media itself.</summary>
public sealed record CollectionSet
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public Guid? ParentId { get; init; }
    public int Order { get; init; }
}

public sealed record PlaylistEntry(Guid MediaId, Guid? SceneId = null);

public enum BookmarkKind { Bookmark, SceneThumbnail }

public sealed record Bookmark
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid MediaId { get; init; }
    public string Title { get; init; } = "";
    public double PositionSeconds { get; init; }
    public BookmarkKind Kind { get; init; }
    public int DisplayOrder { get; init; }
    public Guid? PreviewAssetId { get; init; }
    public string? Description { get; init; }
    /// <summary>Tags of the bookmark, kept like scene tags (<see cref="Scene.Tags"/>).</summary>
    public List<string> Tags { get; init; } = [];
}

public sealed record Scene
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid MediaId { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
    public int DisplayOrder { get; init; }
    public List<string> Tags { get; init; } = [];
    public Guid? PreviewAssetId { get; init; }
}

public sealed record ImportResult
{
    public MigrationReport? Migration { get; init; }
    /// <summary>For an import from EazyFlixPix 4: which areas of the catalog were complete, partial or absent.</summary>
    public LegacyCompatibilityReport? Compatibility { get; init; }
    public int Imported { get; init; }
    public int Skipped { get; init; }
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed record ScanProgress(string Path, int Processed, int Imported);

/// <summary>Portable catalog format. Paths are retained verbatim and source records are never discarded.</summary>
public sealed record CatalogDocument
{
    public string Format { get; init; } = "flixypix-catalog";
    public int Version { get; init; } = 4;
    public List<MediaItem> Items { get; init; } = [];
    public List<Playlist> Playlists { get; init; } = [];
    public List<Scene> Scenes { get; init; } = [];
    public List<SavedSearch> SavedSearches { get; init; } = [];
    public List<FilenameTagRule> FilenameTagRules { get; init; } = [];
    public List<Bookmark> Bookmarks { get; init; } = [];
    public List<MediaAsset> MediaAssets { get; init; } = [];
    public CatalogPreferences? Preferences { get; init; }
    public List<string> LegacyRecords { get; init; } = [];
    public List<TagPreset> TagPresets { get; init; } = [];
    public List<LegacySearchDraft> LegacySearchDrafts { get; init; } = [];
    public List<LegacyJobDraft> LegacyJobDrafts { get; init; } = [];
    public List<LegacyParserDraft> LegacyParserDrafts { get; init; } = [];
    public List<TagAsset> TagAssets { get; init; } = [];
    public List<HistoricalParserDefinition> HistoricalParserDefinitions { get; init; } = [];
    public List<LegacyCombineList> LegacyCombineLists { get; init; } = [];
    public List<HistoricalContentFolder> HistoricalContentFolders { get; init; } = [];
    public List<System.Text.Json.JsonElement> MigrationState { get; init; } = [];
    public List<System.Text.Json.JsonElement> LegacyBlobs { get; init; } = [];
}

public sealed record CatalogPreferences
{
    public SidebarPreferences Sidebar { get; init; } = new();
    public List<string?>? QuickTags { get; init; }
    public List<FolderFavorite> FolderFavorites { get; init; } = [];
    public List<LibraryFolder> LibraryFolders { get; init; } = [];
    public List<TagVocabularyEntry> TagVocabulary { get; init; } = [];
    /// <summary>Roles of tag categories that differ from the built-in default (see TagRoles).</summary>
    public List<TagCategoryRole> TagCategoryRoles { get; init; } = [];
    public List<FilenameParserPreset> FilenameParsers { get; init; } = [];
    public int VideoFavoriteMinimumRating { get; init; } = 6;
    public int ImageFavoriteMinimumRating { get; init; } = 6;
    public int AudioFavoriteMinimumRating { get; init; } = 6;
    public PlaybackPreferences Playback { get; init; } = new();
    public PreviewPreferences Previews { get; init; } = new();
    public GeneralPreferences General { get; init; } = new();
    public SlideshowPreferences Slideshow { get; init; } = new();
    public NfoPreferences Nfo { get; init; } = new();
    public HomePreferences Home { get; init; } = new();
    public HistoryPreferences History { get; init; } = new();
    /// <summary>Media the user marked as no duplicates of each other (similar media).</summary>
    public List<DuplicatePair> NotDuplicates { get; init; } = [];
}

/// <summary>Library behaviour from the original's general settings that still applies today.</summary>
/// <summary>Badges on tiles: resolution (4K, 1080p …), HDR, high frame rate, video codec, number of files.</summary>
public enum TileBadge { Resolution, Hdr, FrameRate, Codec, Versions }

public sealed record GeneralPreferences
{
    public bool CropThumbnails { get; init; }
    /// <summary>Original "Search hidden folders": hidden subfolders are skipped during folder imports unless enabled.</summary>
    public bool IncludeHiddenFolders { get; init; }
    /// <summary>Inverse of the original "Never prompt when deleting media"; permanent file deletion always asks.</summary>
    public bool ConfirmRemove { get; init; } = true;
    /// <summary>Original "Show media extension".</summary>
    public bool ShowExtension { get; init; }
    /// <summary>Original "Highlight videos when not found", for all media kinds.</summary>
    public bool MarkMissingFiles { get; init; } = true;
    /// <summary>Original "Always show import errors": the import report also opens without errors.</summary>
    public bool AlwaysShowImportReport { get; init; }
    /// <summary>Original "Always load last view on startup".</summary>
    public bool RestoreLastView { get; init; }
    /// <summary>What a restore brings back; the original only knew the view, FlixyPix restores everything by default.</summary>
    public RestoreScope RestoreScope { get; init; } = RestoreScope.All;
    /// <summary>Original image viewer mouse wheel command (1 zoom, 2 navigate, 3/4 scroll); default navigate as in the original.</summary>
    public ImageWheelAction ImageWheel { get; init; } = ImageWheelAction.Navigate;
    /// <summary>Tiles show the title; off: the file name (original "displayvideotitle").</summary>
    public bool ShowTitles { get; init; } = true;
    /// <summary>Long titles wrap onto a second line instead of being cut (original "displaymoviename").</summary>
    public bool FullTitles { get; init; }
    /// <summary>Progress bar on started videos (original "showvideoprogress").</summary>
    public bool ShowProgress { get; init; } = true;
    /// <summary>A click on a tile's picture plays the medium instead of selecting it (original "clickvidthumb" = "Plays video").</summary>
    public bool ClickPlays { get; init; }
    /// <summary>Tiles lift slightly under the mouse (original "enablemousehoverzoomthumbnail").</summary>
    public bool HoverLift { get; init; } = true;
    /// <summary>The page bar under the grid (original "pageselectioncontrols").</summary>
    public bool ShowPager { get; init; } = true;
    /// <summary>Steps that back and forward keep (original "navigationhistorystore").</summary>
    public int HistoryLength { get; init; } = 50;
    /// <summary>The main window comes back at its place and size (original "rememberform").</summary>
    public bool RememberWindow { get; init; } = true;
    /// <summary>Several tag filters match when a medium has at least one of them, instead of all (original "videotagmatchtype" = "match").</summary>
    public bool TagsMatchAny { get; init; }
    /// <summary>Characters dropped from titles made from file names at import (original "fileexclusionchars").</summary>
    public string TitleRemoveCharacters { get; init; } = "";
    /// <summary>Characters that become spaces in titles made from file names at import (original "filereplacechars").</summary>
    public string TitleSpaceCharacters { get; init; } = "";
    /// <summary>Folder imports skip images whose file name contains one of these parts (original "filteroutpic").</summary>
    public List<string> SkipImageNames { get; init; } = [];
    /// <summary>Images cross-fade when the viewer moves to the next one (original "image_fade").</summary>
    public bool ImageFade { get; init; }
    /// <summary>Deleting a file moves it to the recycle bin instead of removing it for good (original "deletetorecyclebin").</summary>
    public bool DeleteToRecycleBin { get; init; } = true;
    /// <summary>Badges on tiles (package P6), each switchable; the resolution badge was always on before.</summary>
    public List<TileBadge> TileBadges { get; init; } = [TileBadge.Resolution, TileBadge.Hdr, TileBadge.FrameRate, TileBadge.Versions];
    /// <summary>The sidebar shows how many media each folder holds, subfolders included (original "showvideofoldercounts").</summary>
    public bool ShowFolderCounts { get; init; } = true;
    /// <summary>The category choice shows how many media each category has (original "showvideocategorycounts").</summary>
    public bool ShowCategoryCounts { get; init; } = true;
    /// <summary>The tag categories show how many media have one of their tags (original "showvideotagcategorycounts").</summary>
    public bool ShowTagCategoryCounts { get; init; } = true;

    public const int MaximumHistoryLength = 500;

    /// <summary>The title a new medium gets from its file name: removed characters gone, replaced ones as spaces, spaces
    /// collapsed; an empty result keeps the plain name.</summary>
    public string ImportTitle(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (TitleRemoveCharacters.Length == 0 && TitleSpaceCharacters.Length == 0) return name;
        var text = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            if (TitleSpaceCharacters.Contains(c)) text.Append(' ');
            else if (!TitleRemoveCharacters.Contains(c)) text.Append(c);
        var title = string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return title.Length > 0 ? title : name;
    }

    /// <summary>Whether a folder import leaves this image out because of its name.</summary>
    public bool SkipsImage(string path) =>
        SkipImageNames.Any(part => System.IO.Path.GetFileName(path).Contains(part, StringComparison.OrdinalIgnoreCase));
}

public enum ImageWheelAction { Navigate, Zoom, Scroll }

public sealed record SavedSearch
{
    /// <summary>The whole view as saved (filters, sort, display); older saved searches only have the fields below.</summary>
    public ViewState? State { get; init; }
    /// <summary>Symbol in the sidebar (Segoe Fluent Icons glyph).</summary>
    public string? Glyph { get; init; }
    /// <summary>Place in the sidebar; equal places are sorted by name.</summary>
    public int Order { get; init; }
    /// <summary>When the search was opened last; media imported later count as new.</summary>
    public DateTimeOffset? LastOpenedAt { get; init; }
    public SearchExpression? Expression { get; init; }
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string? Text { get; init; }
    public string? Tag { get; init; }
    public int MinimumRating { get; init; }
    public bool? IsFavorite { get; init; }
    public bool? WatchLater { get; init; }
    public string? Category { get; init; }
    public string? TagCategory { get; init; }
    /// <summary>The collection set the smart collection lies in; null at the top level.</summary>
    public Guid? ParentId { get; init; }
}

/// <summary>A reconstruction rule: filename substring comparison uses OrdinalIgnoreCase.</summary>
public sealed record FilenameTagRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string FilenameContains { get; init; } = "";
    public List<string> Tags { get; init; } = [];
    public bool Enabled { get; init; } = true;
    public List<TagRuleGroup> Groups { get; init; } = [];
    public Dictionary<string, string?> Extra { get; init; } = new();
}

public enum TagRuleField { Filename, FilenameWithExtension, Path, Title, Tag }
public enum TagRuleComparison { Contains, Matches, HasTag }
public enum TagRuleMatch { All, Any }
public sealed record TagRuleCondition(TagRuleField Field, TagRuleComparison Comparison, string Value);
public sealed record TagRuleGroup
{
    public TagRuleMatch Match { get; init; }
    public List<TagRuleCondition> Conditions { get; init; } = [];
    public List<string> Tags { get; init; } = [];
}
