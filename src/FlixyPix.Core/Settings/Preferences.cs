using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
    public CatalogPreferences GetPreferences()
    {
        lock (gate) { ThrowIfDisposed(); return ReadPreferences(); }
    }

    public CatalogPreferences SavePreferences(CatalogPreferences preferences)
    {
        ValidatePreferences(preferences);
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            SaveDocument("preferences", Guid.Empty, preferences, transaction);
            transaction.Commit();
            return preferences;
        }
    }

    public bool IsFavorite(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return IsFavoriteCore(item, GetPreferences());
    }

    private CatalogPreferences ReadPreferences(SqliteTransaction? transaction = null)
    {
        var preferences = ReadDocuments<CatalogPreferences>("preferences", transaction).FirstOrDefault() ?? new();
        // Libraries saved before an action existed get its default shortcut.
        if (preferences.Playback.Hotkeys.Count != PlayerActions.All.Count)
            preferences = preferences with { Playback = preferences.Playback with { Hotkeys = PlayerActions.Complete(preferences.Playback.Hotkeys) } };
        // The wheel used to change the volume by default; libraries from that time seek once, later choices stay.
        if (preferences.Playback.WheelRevision < PlaybackPreferences.CurrentWheelRevision)
            preferences = preferences with { Playback = preferences.Playback with { WheelRevision = PlaybackPreferences.CurrentWheelRevision,
                Wheel = preferences.Playback.Wheel == PlayerWheelAction.Volume ? PlayerWheelAction.Seek : preferences.Playback.Wheel } };
        // The right click did nothing by default; libraries from that time get the player menu once, later choices stay.
        if (preferences.Playback.MouseRevision < PlaybackPreferences.CurrentMouseRevision)
            preferences = preferences with { Playback = preferences.Playback with { MouseRevision = PlaybackPreferences.CurrentMouseRevision,
                RightClick = preferences.Playback.RightClick == PlayerMouseAction.Nothing ? PlayerMouseAction.ContextMenu : preferences.Playback.RightClick } };
        return preferences;
    }

    private static bool IsFavoriteCore(MediaItem item, CatalogPreferences preferences) => item.IsFavorite || item.Rating >= (item.Kind switch
    {
        MediaKind.Image => preferences.ImageFavoriteMinimumRating,
        MediaKind.Video => preferences.VideoFavoriteMinimumRating,
        _ => preferences.AudioFavoriteMinimumRating
    });

    private static void ValidatePreferences(CatalogPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if(preferences.FolderFavorites==null||preferences.TagVocabulary==null||preferences.FilenameParsers==null||
            preferences.FolderFavorites.Any(f=>f==null||string.IsNullOrWhiteSpace(f.Path)||f.Kind is {} kind&&!Enum.IsDefined(kind))||
            preferences.TagVocabulary.Any(t=>t==null||string.IsNullOrWhiteSpace(t.Tag)||string.IsNullOrWhiteSpace(t.Category)||t.Aliases?.Any(string.IsNullOrWhiteSpace)==true)||
            preferences.TagCategoryRoles==null||preferences.TagCategoryRoles.Any(r=>r==null||string.IsNullOrWhiteSpace(r.Category)||!Enum.IsDefined(r.Role))||
            preferences.TagCategoryRoles.GroupBy(r=>r.Category.Trim(),StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))throw new ArgumentException("Invalid catalog favorites, vocabulary or category roles.");
        foreach(var preset in preferences.FilenameParsers)ValidateParser(preset);
        if (preferences.Home?.Rows is null || preferences.Home.Rows.Any(r => !Enum.IsDefined(r))) throw new ArgumentException("Unknown row of the start page.", nameof(preferences));
        if (preferences.Home.RecentDays is < 1 or > HomePreferences.MaximumRecentDays) throw new ArgumentException(T("“Recently watched” covers 1 to 365 days."), nameof(preferences));
        if (preferences.General is not { } general || general.HistoryLength is < 1 or > GeneralPreferences.MaximumHistoryLength || general.TitleRemoveCharacters is null ||
            general.TitleSpaceCharacters is null || general.SkipImageNames is null || general.SkipImageNames.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException(T("Back/forward remembers 1 to 500 steps; skipped image names must not be empty."), nameof(preferences));
        if (preferences.ImageFavoriteMinimumRating is < 0 or > 10 || preferences.VideoFavoriteMinimumRating is < 0 or > 10 || preferences.AudioFavoriteMinimumRating is < 0 or > 10)
            throw new ArgumentException("Favorite thresholds must be between 0 and 10.", nameof(preferences));
    }
}
