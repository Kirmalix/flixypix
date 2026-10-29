using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
    private void ImportLegacyCollections(Func<string, IEnumerable<JsonElement>> rows, string source,
        LegacyMediaMap media, SqliteTransaction transaction, List<string> errors, List<string> warnings)
    {
        IEnumerable<JsonElement> Rows(string table) => rows(table);
        foreach (var (key, item) in media)
        {
            var legacyId = item.LegacyId!;
            var remote = legacyId[(legacyId.IndexOf(':') + 1)..];
            var type = item.Kind == MediaKind.Image ? "9" : "8";
            if (LegacyDescription(type,remote,transaction,warnings) is {} description)
                media[key] = UpsertCore(item with { Description = description }, transaction);
        }
        foreach (var values in Rows("videoclips"))
        {
            var id = Text(values, "ID");
            var video = Text(values, "videoid");
            var start = Number(values, "starttime");
            var end = Number(values, "endtime");
            if (id is null || video is null || !media.TryGetValue("movies:" + video, out var item) ||
                start is null or < 0 || end is null || end <= start)
            { warnings.Add($"videoclips/{id}: clip range or video reference unresolved; original row retained."); continue; }
            var scene = new Scene
            {
                Id = StableLegacyId(source, ":videoclips:" + id), MediaId = item.Id,
                Title = Text(values, "description") ?? "", StartSeconds = start.Value, EndSeconds = end.Value,
                Description = LegacyDescription("25",id,transaction,warnings)
            };
            SaveScene(scene, transaction);
            AuditLegacy("videoclips",id,"partially-projected","clip-range",transaction,scene.Id);
        }

        // Original assign-keywords path uses type 15 and remoteID = videoclips.ID.
        foreach(var values in Rows("keyword"))
        {
            if(Number(values,"type")!=15 || Text(values,"remoteID") is not {} remote)continue;
            var scene=ReadEntity<Scene>("scenes",StableLegacyId(source,":videoclips:"+remote),transaction);if(scene==null)continue;
            var tags=Text(values,"keytags");
            if(string.IsNullOrWhiteSpace(tags))continue;
            if(tags.Length<2 || tags[0]!='"' || tags[^1]!='"')
            { warnings.Add($"keyword/{Text(values,"ID")}: unrecognized clip tag encoding retained verbatim."); continue; }
            var decoded=tags[1..^1].Split("\" \"",StringSplitOptions.RemoveEmptyEntries);
            if(decoded.Any(tag=>tag.Contains('"')))
            { warnings.Add($"keyword/{Text(values,"ID")}: ambiguous quoted clip tags retained verbatim."); continue; }
            try { var updated=scene with { Tags=NormalizeSceneTags(scene.Tags.Concat(decoded)) }; SaveScene(updated,transaction); AuditLegacy("keyword",Text(values,"ID"),"partially-projected","clip-tags",transaction,scene.Id); }
            catch(ArgumentException error) { warnings.Add($"keyword/{Text(values,"ID")}: {error.Message} Original clip tags retained verbatim."); }
        }

        foreach (var table in new[] { "videobookmarks", "videoscenes" })
        foreach (var values in Rows(table))
        {
            var id = Text(values, "ID");
            var video = Text(values, "videoid");
            var time = Number(values, table == "videobookmarks" ? "bookmarktime" : "secs");
            if (id is null || video is null || !media.TryGetValue("movies:" + video, out var item) || time is null or < 0)
            { warnings.Add($"{table}/{id}: timepoint or video reference unresolved; original row retained."); continue; }
            var bookmark=new Bookmark
            {
                Id = StableLegacyId(source, ":" + table + ":" + id), MediaId = item.Id,
                PositionSeconds = time.Value, Kind = table == "videobookmarks" ? BookmarkKind.Bookmark : BookmarkKind.SceneThumbnail,
                Title = Text(values, "description") ?? ""
            };
            SaveBookmark(bookmark, transaction);
            AuditLegacy(table,id,"partially-projected","media-timepoint",transaction,bookmark.Id);
        }

        using var namesCommand=Command("SELECT DISTINCT json_extract(record,'$.values.description') FROM temp.legacy_stage WHERE table_name='videoplaylists' COLLATE NOCASE ORDER BY 1",transaction);
        using var namesReader=namesCommand.ExecuteReader();
        while(namesReader.Read())
        {
            var name=namesReader.IsDBNull(0)?null:namesReader.GetString(0);
            if (string.IsNullOrWhiteSpace(name)) { warnings.Add("videoplaylists: unnamed playlist rows retained without projection."); continue; }
            var entries = new List<PlaylistEntry>();
            foreach (var values in ReadLegacyPlaylistRows(name,transaction))
            {
                var video = Text(values, "videoid");
                if (video is null || !media.TryGetValue("movies:" + video, out var item))
                { warnings.Add($"videoplaylists/{Text(values, "ID")}: missing video; entry retained raw."); continue; }
                var clip = Number(values, "bookmarkid");
                if (clip == 0) entries.Add(new PlaylistEntry(item.Id));
                else if (clip is > 0 && ReadEntity<Scene>("scenes",StableLegacyId(source,":videoclips:"+Text(values,"bookmarkid")),transaction) is {} scene && scene.MediaId == item.Id)
                    entries.Add(new PlaylistEntry(item.Id, scene.Id));
                else warnings.Add($"videoplaylists/{Text(values, "ID")}: unresolved clip reference; entry retained raw.");
            }
            var playlist = new Playlist
            {
                Id = StableLegacyId(source, ":videoplaylists:" + name), Name = name,
                MediaIds = entries.Select(x => x.MediaId).ToList(), Entries = entries,
                Extra = new() { ["legacy.table"] = "videoplaylists", ["legacy.sourceSha256"] = source,
                    ["legacy.rowCount"] = entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            };
            try { ValidatePlaylistEntries(playlist, transaction); SavePlaylist(playlist, transaction); }
            catch (ArgumentException ex) { errors.Add($"videoplaylists/{name}: {ex.Message}"); }
        }
    }
}
