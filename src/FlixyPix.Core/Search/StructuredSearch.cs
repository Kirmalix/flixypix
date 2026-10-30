using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public enum SearchOperator { Condition, All, Any, Not }
public enum SearchField
{
    Text, Title, Path, Description, Tag, TagCategory, Category, Rating, AddedAt,
    Duration, FileSize, Width, Height, Kind, WatchLater, Favorite, PlaybackPosition, CategorizedTag, PlayCount, ViewCount,
    Filename, Extension, Directory, FileDate,
    /// <summary>Height greater than width; false when not (square included), unknown without both dimensions.</summary>
    Portrait,
    /// <summary>Bit rate in kbit/s as ffprobe read it, otherwise the average from file size and duration; unknown without either.</summary>
    Bitrate,
    /// <summary>The medium's release date (<see cref="MediaItem.ReleaseDate"/>).</summary>
    ReleaseDate,
    /// <summary>A performer or studio of the medium whose profile matches; <see cref="SearchCondition.Profile"/> says which property.</summary>
    Profile,
    /// <summary>The O counter (<see cref="MediaItem.OCount"/>).</summary>
    OCount,
    /// <summary>Playbacks watched to the watched share; media without history count their plays (<see cref="MediaItem.WatchCount"/>).</summary>
    WatchCount,
    /// <summary>A tag of one of the medium's bookmarks or scenes; NotEquals means none of them has it.</summary>
    MarkTag,
    /// <summary>ffprobe name of the video codec, such as "h264" or "hevc"; unknown until the technical data was read.</summary>
    VideoCodec,
    /// <summary>Frames per second of the video.</summary>
    FrameRate,
    /// <summary>High dynamic range (PQ or HLG); unknown until the technical data was read.</summary>
    Hdr,
    /// <summary>Number of files of the medium: 1 plus its further versions.</summary>
    Versions,
    /// <summary>Lines of the picture: the shorter side, as the resolution classes count.</summary>
    Lines, HasThumbnail
}
public enum SearchComparison
{
    Contains, Equals, NotEquals, StartsWith, EndsWith, GreaterThan, GreaterOrEqual,
    LessThan, LessOrEqual, IsKnown, IsUnknown
}

/// <summary>Exactly one typed value is required, except IsKnown/IsUnknown which require no value.</summary>
public sealed record SearchCondition
{
    public SearchField Field { get; init; }
    public SearchComparison Comparison { get; init; }
    public string? Text { get; init; }
    public string? TagCategory { get; init; }
    public double? Number { get; init; }
    public DateTimeOffset? Date { get; init; }
    public bool? Flag { get; init; }
    /// <summary>Only for <see cref="SearchField.Profile"/>: the property of the profile that is compared.</summary>
    public ProfileProperty? Profile { get; init; }
}

/// <summary>A bounded, data-only search tree; conditions and groups never contain SQL.</summary>
public sealed record SearchExpression
{
    public SearchOperator Operator { get; init; }
    public List<SearchExpression> Children { get; init; } = [];
    public SearchCondition? Condition { get; init; }
    public static SearchExpression Where(SearchCondition condition) => new() { Condition = condition };
    public static SearchExpression All(params SearchExpression[] children) => new() { Operator = SearchOperator.All, Children = children.ToList() };
    public static SearchExpression Any(params SearchExpression[] children) => new() { Operator = SearchOperator.Any, Children = children.ToList() };
    public static SearchExpression Not(SearchExpression child) => new() { Operator = SearchOperator.Not, Children = [child] };
}

public sealed partial class CatalogRepository
{
    /// <summary>Rejects invalid field/value combinations and trees deeper than 16 or larger than 256 nodes.</summary>
    public static void ValidateSearchExpression(SearchExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var nodes = 0;
        Validate(expression, 1);
        void Validate(SearchExpression node, int depth)
        {
            if (node is null || depth > 16 || ++nodes > 256 || !Enum.IsDefined(node.Operator) || node.Children is null)
                throw new ArgumentException("Search expression must contain at most 256 nodes and 16 levels.", nameof(expression));
            if (node.Operator == SearchOperator.Condition)
            {
                if (node.Condition is null || node.Children.Count != 0) throw new ArgumentException("A search condition cannot contain child expressions.");
                ValidateCondition(node.Condition); return;
            }
            if (node.Condition is not null || node.Children.Count == 0 || node.Operator == SearchOperator.Not && node.Children.Count != 1)
                throw new ArgumentException("All/Any require child expressions; Not requires exactly one child and no condition.");
            foreach (var child in node.Children) Validate(child, depth + 1);
        }
    }

    private static bool NumericField(SearchField field) => field is SearchField.Rating or SearchField.Duration or SearchField.FileSize or SearchField.Width or SearchField.Height or SearchField.PlaybackPosition or SearchField.PlayCount or SearchField.ViewCount or SearchField.Bitrate or SearchField.OCount or SearchField.WatchCount or SearchField.FrameRate or SearchField.Versions or SearchField.Lines;
    private static bool BooleanField(SearchField field) => field is SearchField.HasThumbnail or SearchField.WatchLater or SearchField.Favorite or SearchField.Portrait or SearchField.Hdr;
    private static bool DateField(SearchField field) => field is SearchField.AddedAt or SearchField.FileDate or SearchField.ReleaseDate;
    private static bool SetField(SearchField field) => field is SearchField.Tag or SearchField.TagCategory;
    private static bool PresenceComparison(SearchComparison comparison) => comparison is SearchComparison.IsKnown or SearchComparison.IsUnknown;
    private static void ValidateCondition(SearchCondition condition)
    {
        if (!Enum.IsDefined(condition.Field) || !Enum.IsDefined(condition.Comparison)) throw new ArgumentException("Unknown search field or comparison.");
        if(condition.Field==SearchField.CategorizedTag){ValidateCategorizedTagCondition(condition);return;}
        if (condition.Field == SearchField.Profile) { ValidateProfileCondition(condition); return; }
        if (condition.Profile is not null) throw new ArgumentException("Only profile conditions name a profile property.");
        if(condition.TagCategory!=null)throw new ArgumentException("Only categorized tag conditions accept a category value.");
        var values = (condition.Text is null ? 0 : 1) + (condition.Number is null ? 0 : 1) + (condition.Date is null ? 0 : 1) + (condition.Flag is null ? 0 : 1);
        if (PresenceComparison(condition.Comparison))
        {
            if (values != 0) throw new ArgumentException("Known/unknown conditions have no value.");
            return;
        }
        if (values != 1) throw new ArgumentException("A condition requires exactly one typed value.");
        if (NumericField(condition.Field))
        {
            if (condition.Number is not { } number || !double.IsFinite(number) || number < 0 ||
                condition.Field == SearchField.Rating && number > 10 ||
                condition.Comparison is not (SearchComparison.Equals or SearchComparison.NotEquals or SearchComparison.GreaterThan or SearchComparison.GreaterOrEqual or SearchComparison.LessThan or SearchComparison.LessOrEqual))
                throw new ArgumentException("Numeric conditions require a finite nonnegative number and a numeric comparison (ratings 0–10).");
        }
        else if (DateField(condition.Field))
        {
            if (condition.Date is null || condition.Comparison is not (SearchComparison.Equals or SearchComparison.NotEquals or SearchComparison.GreaterThan or SearchComparison.GreaterOrEqual or SearchComparison.LessThan or SearchComparison.LessOrEqual))
                throw new ArgumentException("Date conditions require a date/time and a numeric comparison.");
        }
        else if (BooleanField(condition.Field))
        {
            if (condition.Flag is null || condition.Comparison is not (SearchComparison.Equals or SearchComparison.NotEquals)) throw new ArgumentException("Boolean conditions require a boolean equality comparison.");
        }
        else
        {
            if (condition.Text is not { Length: > 0 and <= 4096 } || condition.Comparison is not
                (SearchComparison.Contains or SearchComparison.Equals or SearchComparison.NotEquals or SearchComparison.StartsWith or SearchComparison.EndsWith))
                throw new ArgumentException("Text conditions require 1–4096 characters and a text comparison.");
            if (condition.Field == SearchField.Kind && (!Enum.TryParse<MediaKind>(condition.Text, true, out var kind) || !Enum.IsDefined(kind) ||
                condition.Comparison is not (SearchComparison.Equals or SearchComparison.NotEquals)))
                throw new ArgumentException("Media kind requires Image, Video or Audio and equality comparison.");
        }
    }

    private string BuildExpressionSql(SearchExpression expression, Func<object, string> parameter, SqliteTransaction? transaction)
    {
        if (expression.Operator == SearchOperator.Not) return "NOT (" + BuildExpressionSql(expression.Children[0], parameter, transaction) + ")";
        if (expression.Operator is SearchOperator.All or SearchOperator.Any)
            return "(" + string.Join(expression.Operator == SearchOperator.All ? " AND " : " OR ", expression.Children.Select(child => BuildExpressionSql(child, parameter, transaction))) + ")";
        var condition = expression.Condition!;
        if(condition.Field==SearchField.CategorizedTag)return BuildCategorizedTagSql(condition,parameter);
        if (condition.Field == SearchField.Profile) return BuildProfileSql(condition, parameter, transaction);
        if (condition.Field == SearchField.MarkTag) return BuildMarkTagSql(condition, parameter);
        var column = condition.Field switch
        {
            SearchField.HasThumbnail => "(SELECT available FROM media_thumbnail_state WHERE media_id=q.id)",
            SearchField.Text => "q.search_text", SearchField.Title => "q.title", SearchField.Path => "q.path",
            SearchField.Filename => "efp_filename(q.path)", SearchField.Extension => "efp_extension(q.path)",
            SearchField.Directory => "efp_directory(q.path)",
            SearchField.Description => "(SELECT json_extract(document,'$.description') FROM media WHERE id=q.id)",
            SearchField.Category => "q.category", SearchField.Rating => "q.rating", SearchField.AddedAt => "q.imported", SearchField.FileDate => "q.file_date",
            SearchField.ReleaseDate => "(SELECT julianday(json_extract(document,'$.releaseDate')) FROM media WHERE id=q.id)",
            SearchField.Duration => "q.duration", SearchField.FileSize => "q.file_size", SearchField.Kind => "q.kind",
            SearchField.WatchLater => "q.watch_later", SearchField.Width => "(SELECT json_extract(document,'$.width') FROM media WHERE id=q.id)",
            SearchField.Height => "(SELECT json_extract(document,'$.height') FROM media WHERE id=q.id)",
            SearchField.PlaybackPosition => "(SELECT coalesce(json_extract(document,'$.playbackPositionSeconds'),0) FROM media WHERE id=q.id)",
            SearchField.PlayCount => "(SELECT json_extract(document,'$.playCount') FROM media WHERE id=q.id)",
            SearchField.ViewCount => "(SELECT json_extract(document,'$.viewCount') FROM media WHERE id=q.id)",
            SearchField.OCount => "q.o_count",
            SearchField.WatchCount => "(SELECT coalesce(json_extract(document,'$.watchCount'),json_extract(document,'$.playCount')) FROM media WHERE id=q.id)",
            SearchField.Tag => "t.tag", SearchField.TagCategory => "t.category",
            SearchField.Favorite => FavoriteExpression(),
            SearchField.Portrait => "(SELECT CASE WHEN json_extract(document,'$.width') IS NULL OR json_extract(document,'$.height') IS NULL THEN NULL " +
                "WHEN json_extract(document,'$.height')>json_extract(document,'$.width') THEN 1 ELSE 0 END FROM media WHERE id=q.id)",
            SearchField.Bitrate => "q.bit_rate", SearchField.VideoCodec => "q.video_codec", SearchField.FrameRate => "q.frame_rate",
            SearchField.Hdr => "q.hdr", SearchField.Versions => "q.versions", SearchField.Lines => "q.lines",
            _ => throw new ArgumentException("Unknown search field.")
        };
        if (SetField(condition.Field))
        {
            if (PresenceComparison(condition.Comparison))
            {
                var any = $"EXISTS (SELECT 1 FROM media_tags t WHERE t.media_id=q.id AND {column}<>'')";
                return condition.Comparison == SearchComparison.IsKnown ? any : "NOT " + any;
            }
            // NotEquals means the set does not contain that value, rather than one nonmatching tag exists.
            var positive = condition.Comparison == SearchComparison.NotEquals ? condition with { Comparison = SearchComparison.Equals } : condition;
            var exists = $"EXISTS (SELECT 1 FROM media_tags t WHERE t.media_id=q.id AND {ScalarComparison(column, positive)})";
            return condition.Comparison == SearchComparison.NotEquals ? "NOT " + exists : exists;
        }
        return ScalarComparison(column, condition);

        string FavoriteExpression()
        {
            var preferences = ReadPreferences(transaction);
            return $"(q.pinned=1 OR q.rating >= CASE q.kind WHEN 'Image' THEN {preferences.ImageFavoriteMinimumRating} WHEN 'Video' THEN {preferences.VideoFavoriteMinimumRating} ELSE {preferences.AudioFavoriteMinimumRating} END)";
        }
        string ScalarComparison(string value, SearchCondition condition)
        {
            if (PresenceComparison(condition.Comparison)) return value + (condition.Comparison == SearchComparison.IsKnown ? " IS NOT NULL" : " IS NULL");
            object input = condition.Number is { } number ? number : condition.Date is { } date ? date.ToString("O") :
                condition.Flag is { } flag ? flag ? 1 : 0 : condition.Field == SearchField.Kind ? Enum.Parse<MediaKind>(condition.Text!, true).ToString() : condition.Text!;
            var argument = parameter(input);
            if (DateField(condition.Field)) argument = "julianday(" + argument + ")";
            var comparison = condition.Comparison switch
            {
                SearchComparison.Contains => $"efp_contains({value},{argument})",
                SearchComparison.StartsWith => $"efp_starts({value},{argument})",
                SearchComparison.EndsWith => $"efp_ends({value},{argument})",
                SearchComparison.Equals => $"{value}={argument}" + (condition.Text is not null ? " COLLATE EFP_ORDINAL" : ""),
                SearchComparison.NotEquals => $"{value}<>{argument}" + (condition.Text is not null ? " COLLATE EFP_ORDINAL" : ""),
                SearchComparison.GreaterThan => $"{value}>{argument}", SearchComparison.GreaterOrEqual => $"{value}>={argument}",
                SearchComparison.LessThan => $"{value}<{argument}", SearchComparison.LessOrEqual => $"{value}<={argument}",
                _ => throw new ArgumentException("Unknown search comparison.")
            };
            // Missing scalar values never satisfy comparisons, including NotEquals; an explicit Not flips that result.
            return "coalesce((" + comparison + "),0)";
        }
    }

    /// <summary>Bookmarks and scenes keep their tags in the document; both tables have an index by medium.</summary>
    private static string BuildMarkTagSql(SearchCondition condition, Func<object, string> parameter)
    {
        static string Exists(string test) =>
            $"(EXISTS (SELECT 1 FROM bookmarks b, json_each(b.document,'$.tags') j WHERE b.media_id=q.id{test}) OR " +
            $"EXISTS (SELECT 1 FROM scenes s, json_each(s.document,'$.tags') j WHERE s.media_id=q.id{test}))";
        if (PresenceComparison(condition.Comparison)) return (condition.Comparison == SearchComparison.IsKnown ? "" : "NOT ") + Exists("");
        var argument = parameter(condition.Text!);
        var test = condition.Comparison switch
        {
            SearchComparison.Contains => $"efp_contains(j.value,{argument})", SearchComparison.StartsWith => $"efp_starts(j.value,{argument})",
            SearchComparison.EndsWith => $"efp_ends(j.value,{argument})", _ => $"j.value={argument} COLLATE EFP_ORDINAL",
        };
        return (condition.Comparison == SearchComparison.NotEquals ? "NOT " : "") + Exists(" AND " + test);
    }
}
