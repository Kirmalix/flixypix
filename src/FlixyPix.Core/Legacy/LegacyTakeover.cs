using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public enum LegacyEntryState { Adopt, AlreadyAdopted, Kept, Orphan }

/// <summary>What the takeover would do with one data group: files found, how many are new, already taken over, kept because
/// FlixyPix already has its own picture, orphans of deleted media, and catalog entries without a file.</summary>
public sealed record LegacyTakeoverGroup(LegacyDataGroup Group, int Files, long Bytes, int Adopt, int AlreadyAdopted, int Kept, int Orphans, int Missing);

internal sealed record LegacyCacheEntry(LegacyDataGroup Group, string Path, long Length, DateTime LastWriteUtc, Guid Target, LegacyEntryState State);

public sealed record LegacyTakeoverPlan
{
    public required string Batch { get; init; }
    public required string CacheFolder { get; init; }
    public bool ReplaceThumbnails { get; init; }
    public IReadOnlyList<LegacyTakeoverGroup> Groups { get; init; } = [];
    public LegacyTagImagePreview? TagImages { get; init; }
    public required LegacySettingsPlan Settings { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = [];
    internal IReadOnlyList<LegacyCacheEntry> Entries { get; init; } = [];
    public LegacyTakeoverGroup? Group(LegacyDataGroup group) => Groups.FirstOrDefault(g => g.Group == group);
}

public sealed record LegacyTakeoverChoice(IReadOnlyCollection<LegacyDataGroup> Data, IReadOnlyCollection<LegacySettingGroup> Settings);

public sealed record LegacyTakeoverResult(LegacyDataGroup Group, int Adopted, int AlreadyAdopted, int Kept, int Orphans, int Missing, int Invalid);

public sealed record LegacyTakeoverReport(IReadOnlyList<LegacyTakeoverResult> Data, IReadOnlyList<LegacySettingGroup> Settings, int SettingsChanged, IReadOnlyList<string> Problems, TimeSpan Duration);

/// <summary>Takes over pictures the original already made: video thumbnails (vcache) become the media thumbnails, bookmark
/// pictures (bcache) and clip previews (ccache) become preview images of the migrated bookmarks and clips, and tag pictures
/// are matched to the vocabulary. Files are copied, never moved; one transaction commits everything or nothing.</summary>
public sealed partial class CatalogRepository
{
    /// <summary>Provenance of everything taken over; FlixyPix may later replace these thumbnails with its own.</summary>
    public const string LegacyOrigin = "EazyFlixPix 4.8.4";
    /// <summary>The original as named on screen; <see cref="LegacyOrigin"/> stays as the exact version stored with adopted data.</summary>
    public const string LegacyProductName = "EazyFlixPix 4";
    public const string ThumbnailOriginKey = "thumbnail.origin", ThumbnailLegacyFileKey = "thumbnail.legacyFile", ThumbnailLegacyShaKey = "thumbnail.legacySha256";
    private const string ThumbnailStyleKey = "thumbnail.style", CacheFileKey = "legacy.cacheFile";

    /// <summary>Matches the cache files of an installation with the media, bookmarks and clips a migration batch created.
    /// Only file names and sizes are read here; contents are checked during the takeover.</summary>
    public LegacyTakeoverPlan PlanLegacyTakeover(LegacyInstallation installation, string batch, bool replaceThumbnails = false, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        lock (gate)
        {
            ThrowIfDisposed();
            var problems = new List<string>();
            LegacyTagImagePreview? tagImages = null;
            if (Directory.Exists(installation.TagImageFolder))
                try { tagImages = PreviewLegacyTagImages(installation.TagImageFolder, null, token); }
                catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { problems.Add(T("Tag images: {0}", error.Message)); }
            using var transaction = connection.BeginTransaction();
            using (var known = Command("SELECT 1 FROM legacy_imports WHERE batch=$batch", transaction, ("$batch", batch)))
                if (known.ExecuteScalar() is null) throw new ArgumentException(T("This library does not contain the chosen import."));
            var movies = RetainedTargets(batch, "movies", transaction);
            var bookmarks = RetainedTargets(batch, "videobookmarks", transaction);
            var clips = RetainedTargets(batch, "videoclips", transaction);
            var entries = new List<LegacyCacheEntry>(); var groups = new List<LegacyTakeoverGroup>();

            void Scan(LegacyDataGroup group, string folder, Func<string, (Guid? Target, LegacyEntryState State)> match, int known)
            {
                int files = 0, matched = 0; long bytes = 0; var found = new List<LegacyCacheEntry>();
                if (Directory.Exists(folder))
                    foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.jpg", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint }).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        token.ThrowIfCancellationRequested();
                        var (target, state) = match(file.Name);
                        if (state == LegacyEntryState.Orphan && target is null && !Matches(group, file.Name)) continue;
                        files++; bytes += file.Length; if (state != LegacyEntryState.Orphan) matched++;
                        found.Add(new(group, file.FullName, file.Length, file.LastWriteTimeUtc, target ?? Guid.Empty, state));
                    }
                entries.AddRange(found);
                groups.Add(new(group, files, bytes, found.Count(e => e.State == LegacyEntryState.Adopt), found.Count(e => e.State == LegacyEntryState.AlreadyAdopted),
                    found.Count(e => e.State == LegacyEntryState.Kept), found.Count(e => e.State == LegacyEntryState.Orphan), Math.Max(0, known - matched)));
            }

            Scan(LegacyDataGroup.VideoThumbnails, Path.Combine(installation.CacheFolder, "vcache"), name =>
            {
                if (!LegacyInstallations.VideoThumbnailName().IsMatch(name) || !movies.TryGetValue(Path.GetFileNameWithoutExtension(name), out var id) || ReadMediaDocument(id, transaction) is not { } media)
                    return (null, LegacyEntryState.Orphan);
                return (id, ThumbnailState(media, "vcache/" + name, replaceThumbnails));
            }, movies.Count);
            Scan(LegacyDataGroup.BookmarkImages, Path.Combine(installation.CacheFolder, "bcache"), name =>
            {
                if (!LegacyInstallations.BookmarkImageName().IsMatch(name)) return (null, LegacyEntryState.Orphan);
                var parts = Path.GetFileNameWithoutExtension(name).Split('_');
                // The file name carries the video too; a bookmark that now belongs to another medium is not guessed.
                if (!bookmarks.TryGetValue(parts[1], out var id) || !movies.TryGetValue(parts[0], out var video) || ReadEntity<Bookmark>("bookmarks", id, transaction) is not { } bookmark || bookmark.MediaId != video)
                    return (null, LegacyEntryState.Orphan);
                return (id, PreviewState(bookmark.PreviewAssetId, "bcache/" + name, transaction));
            }, bookmarks.Count);
            Scan(LegacyDataGroup.ClipPreviews, Path.Combine(installation.CacheFolder, "ccache"), name =>
            {
                if (!LegacyInstallations.VideoThumbnailName().IsMatch(name) || !clips.TryGetValue(Path.GetFileNameWithoutExtension(name), out var id) || ReadEntity<Scene>("scenes", id, transaction) is not { } scene)
                    return (null, LegacyEntryState.Orphan);
                return (id, PreviewState(scene.PreviewAssetId, "ccache/" + name, transaction));
            }, clips.Count);
            if (tagImages != null)
                groups.Add(new(LegacyDataGroup.TagImages, tagImages.Files.Count, tagImages.Files.Sum(f => f.Length), tagImages.Files.Count(f => f.IsUnambiguous), 0, 0, tagImages.Files.Count(f => !f.IsUnambiguous), 0));
            return new LegacyTakeoverPlan
            {
                Batch = batch, CacheFolder = installation.CacheFolder, ReplaceThumbnails = replaceThumbnails, Groups = groups, TagImages = tagImages,
                Settings = PlanLegacySettingsCore(batch, transaction), Problems = problems, Entries = entries,
            };
        }
    }

    private static bool Matches(LegacyDataGroup group, string name) => group == LegacyDataGroup.BookmarkImages
        ? LegacyInstallations.BookmarkImageName().IsMatch(name) : LegacyInstallations.VideoThumbnailName().IsMatch(name);

    private static LegacyEntryState ThumbnailState(MediaItem media, string legacyFile, bool replace)
    {
        var present = media.ThumbnailPath is { } path && File.Exists(path);
        if (present && media.Extra.GetValueOrDefault(ThumbnailLegacyFileKey) == legacyFile) return LegacyEntryState.AlreadyAdopted;
        if (media.Extra.ContainsKey("thumbnail.assetId")) return LegacyEntryState.Kept;
        return present && !replace ? LegacyEntryState.Kept : LegacyEntryState.Adopt;
    }

    private LegacyEntryState PreviewState(Guid? assetId, string legacyFile, SqliteTransaction transaction)
    {
        if (assetId is null) return LegacyEntryState.Adopt;
        using var command = Command("SELECT json_extract(document,'$.extra.\"" + CacheFileKey + "\"') FROM media_assets WHERE id=$id", transaction, ("$id", assetId.Value.ToString()));
        return command.ExecuteScalar() as string == legacyFile ? LegacyEntryState.AlreadyAdopted : LegacyEntryState.Kept;
    }

    /// <summary>Legacy key → FlixyPix id for the rows of one original table that a batch projected.</summary>
    private Dictionary<string, Guid> RetainedTargets(string batch, string table, SqliteTransaction transaction)
    {
        var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
        using var command = Command("SELECT legacy_key,target_ids FROM migration_rows WHERE batch=$batch AND table_name=$table COLLATE NOCASE AND status='partially-projected' AND legacy_key IS NOT NULL",
            transaction, ("$batch", batch), ("$table", table));
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (JsonSerializer.Deserialize<string[]>(reader.GetString(1), Json) is [var first, ..] && Guid.TryParse(first, out var id)) result[reader.GetString(0)] = id;
        return result;
    }

    private MediaItem? ReadMediaDocument(Guid id, SqliteTransaction? transaction)
    {
        using var command = Command("SELECT document FROM media WHERE id=$id", transaction, ("$id", id.ToString()));
        return command.ExecuteScalar() is string document ? JsonSerializer.Deserialize<MediaItem>(document, Json) : null;
    }

    /// <summary>Copies the chosen groups into the library. Video thumbnails are copied into <paramref name="thumbnailDirectory"/>
    /// first; then a single transaction links them, stores bookmark and clip pictures, tag pictures and the chosen settings.
    /// Any error or cancellation rolls the transaction back and removes the copies, so no half takeover remains.</summary>
    public LegacyTakeoverReport ApplyLegacyTakeover(LegacyTakeoverPlan plan, LegacyTakeoverChoice choice, string thumbnailDirectory,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(choice);
        var watch = Stopwatch.StartNew();
        var directory = Path.GetFullPath(thumbnailDirectory); FileAccessPolicy.Current.EnsureWritable(directory);
        var counts = Enum.GetValues<LegacyDataGroup>().ToDictionary(g => g, _ => new int[6]); // adopted, already, kept, orphans, missing, invalid
        foreach (var group in plan.Groups)
        {
            var c = counts[group.Group];
            (c[1], c[2], c[3], c[4]) = (group.AlreadyAdopted, group.Kept, group.Orphans, group.Missing);
        }
        var created = new List<string>(); var copies = new List<(LegacyCacheEntry Entry, string Target, string Sha)>(); var problems = new List<string>(plan.Problems);
        try
        {
            if (choice.Data.Contains(LegacyDataGroup.VideoThumbnails))
            {
                Directory.CreateDirectory(directory);
                var due = plan.Entries.Where(e => e.Group == LegacyDataGroup.VideoThumbnails && e.State == LegacyEntryState.Adopt).ToList();
                for (var i = 0; i < due.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (i % 100 == 0) progress?.Report(T("Copying thumbnails · {0:N0} of {1:N0}", i, due.Count));
                    if (ReadLegacyPicture(due[i], problems) is not { } data) { counts[LegacyDataGroup.VideoThumbnails][5]++; continue; }
                    var sha = Convert.ToHexStringLower(SHA256.HashData(data));
                    var target = Path.Combine(directory, $"{due[i].Target}-legacy-{sha[..16]}.jpg");
                    if (!File.Exists(target))
                    {
                        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try { File.WriteAllBytes(temporary, data); File.Move(temporary, target, overwrite: false); created.Add(target); }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                    else if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(target))) != sha) throw new InvalidDataException(T("An existing copy in the thumbnail folder was changed: ") + Path.GetFileName(target));
                    copies.Add((due[i], target, sha));
                }
            }
            var replaced = new List<string>(); var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int settingsChanged;
            lock (gate)
            {
                ThrowIfDisposed();
                var tagImages = choice.Data.Contains(LegacyDataGroup.TagImages) && plan.TagImages != null ? VerifyLegacyTagImages(plan.TagImages, token) : null;
                using var transaction = connection.BeginTransaction();
                try
                {
                    progress?.Report(T("Writing the import into the library …"));
                    foreach (var (entry, target, sha) in copies)
                    {
                        token.ThrowIfCancellationRequested();
                        var c = counts[LegacyDataGroup.VideoThumbnails];
                        var legacyFile = "vcache/" + Path.GetFileName(entry.Path);
                        if (ReadMediaDocument(entry.Target, transaction) is not { } media) { c[3]++; continue; }
                        var state = ThumbnailState(media, legacyFile, plan.ReplaceThumbnails);
                        if (state != LegacyEntryState.Adopt) { c[state == LegacyEntryState.AlreadyAdopted ? 1 : 2]++; continue; }
                        if (media.ThumbnailPath is { } old && old.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !old.Equals(target, StringComparison.OrdinalIgnoreCase)) replaced.Add(old);
                        UpsertCore(media with
                        {
                            ThumbnailPath = target,
                            Extra = new(media.Extra) { [ThumbnailOriginKey] = LegacyOrigin, [ThumbnailLegacyFileKey] = legacyFile, [ThumbnailLegacyShaKey] = sha, [ThumbnailStyleKey] = LegacyOrigin },
                        }, transaction);
                        linked.Add(target); c[0]++;
                    }
                    foreach (var group in new[] { LegacyDataGroup.BookmarkImages, LegacyDataGroup.ClipPreviews }.Where(choice.Data.Contains))
                    {
                        var due = plan.Entries.Where(e => e.Group == group && e.State == LegacyEntryState.Adopt).ToList(); var c = counts[group];
                        for (var i = 0; i < due.Count; i++)
                        {
                            token.ThrowIfCancellationRequested();
                            if (i % 500 == 0) progress?.Report(T("{0} · {1:N0} of {2:N0}", (group == LegacyDataGroup.BookmarkImages ? T("Bookmark images") : T("Clip previews")), i, due.Count));
                            AdoptPreview(due[i], c, problems, transaction);
                        }
                    }
                    if (tagImages != null)
                    {
                        progress?.Report(T("Tag images …"));
                        var tags = WriteLegacyTagImages(tagImages, transaction, token);
                        counts[LegacyDataGroup.TagImages][0] = tags.Imported; counts[LegacyDataGroup.TagImages][3] = tags.Unmatched + tags.Ambiguous;
                    }
                    settingsChanged = ApplyLegacySettingsCore(plan.Settings, choice.Settings, transaction);
                    token.ThrowIfCancellationRequested();
                    transaction.Commit();
                }
                catch { transaction.Rollback(); cachedMedia = null; throw; }
            }
            // Committed: the copies are in use. Copies that ended up unused and replaced own thumbnails go.
            foreach (var path in replaced.Concat(created.Where(p => !linked.Contains(p)))) TryDelete(path);
            created.Clear();
            var results = Enum.GetValues<LegacyDataGroup>().Where(choice.Data.Contains)
                .Select(g => new LegacyTakeoverResult(g, counts[g][0], counts[g][1], counts[g][2], counts[g][3], counts[g][4], counts[g][5])).ToList();
            return new(results, choice.Settings.Order().ToList(), settingsChanged, problems, watch.Elapsed);
        }
        finally { foreach (var path in created) TryDelete(path); }
    }

    private void AdoptPreview(LegacyCacheEntry entry, int[] counts, List<string> problems, SqliteTransaction transaction)
    {
        var bookmark = entry.Group == LegacyDataGroup.BookmarkImages ? ReadEntity<Bookmark>("bookmarks", entry.Target, transaction) : null;
        var scene = entry.Group == LegacyDataGroup.ClipPreviews ? ReadEntity<Scene>("scenes", entry.Target, transaction) : null;
        if (bookmark is null && scene is null) { counts[3]++; return; }
        var legacyFile = (entry.Group == LegacyDataGroup.BookmarkImages ? "bcache/" : "ccache/") + Path.GetFileName(entry.Path);
        var state = PreviewState(bookmark != null ? bookmark.PreviewAssetId : scene!.PreviewAssetId, legacyFile, transaction);
        if (state != LegacyEntryState.Adopt) { counts[state == LegacyEntryState.AlreadyAdopted ? 1 : 2]++; return; }
        if (ReadLegacyPicture(entry, problems) is not { } data) { counts[5]++; return; }
        var sha = Convert.ToHexStringLower(SHA256.HashData(data));
        var mediaId = bookmark?.MediaId ?? scene!.MediaId;
        var asset = new MediaAsset
        {
            Id = StableId("legacy-cache:" + legacyFile + ":" + entry.Target + ":" + sha), MediaId = mediaId, Kind = MediaAssetKind.ScenePreview,
            Label = bookmark != null ? T("Bookmark image from {0}", LegacyProductName) : T("Clip preview from {0}", LegacyProductName),
            PositionSeconds = bookmark?.PositionSeconds ?? scene!.StartSeconds, Data = data,
            Extra = new() { [CacheFileKey] = legacyFile, ["legacy.sha256"] = sha, [ThumbnailOriginKey] = LegacyOrigin },
        };
        EnsureMediaExist([mediaId], transaction); ValidateAsset(asset); SaveAsset(asset, transaction);
        if (bookmark != null) SaveBookmark(bookmark with { PreviewAssetId = asset.Id }, transaction);
        else SaveScene(scene! with { PreviewAssetId = asset.Id }, transaction);
        counts[0]++;
    }

    /// <summary>Reads a cache picture, refusing a source that changed since the plan; null for a file that is no JPEG.</summary>
    private static byte[]? ReadLegacyPicture(LegacyCacheEntry entry, List<string> problems)
    {
        using var input = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != entry.Length || File.GetLastWriteTimeUtc(entry.Path) != entry.LastWriteUtc)
            throw new InvalidOperationException(T("A file of the installation has changed since the check. Please check the import again: ") + entry.Path);
        if (input.Length is 0 or > MaximumAssetBytes) { problems.Add(Path.GetFileName(entry.Path) + T(": empty or too large")); return null; }
        var data = new byte[input.Length]; input.ReadExactly(data);
        if (ImageExtension(data) != ".jpg") { problems.Add(Path.GetFileName(entry.Path) + T(": not a JPEG image")); return null; }
        return data;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
