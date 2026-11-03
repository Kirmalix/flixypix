using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed record PortableLocation(string Library, string Application, string Volume)
{
    /// <summary>The catalog was searched once for paths into an earlier library folder that no record names.</summary>
    public bool ManagedPathsChecked { get; init; }
}

/// <summary>Apply the longest matching anchor exactly once, including unchanged anchors.</summary>
internal sealed class StoragePathMap
{
    private readonly PathPrefixMapping[] mappings;
    internal StoragePathMap(IEnumerable<PathPrefixMapping> mappings)
    {
        this.mappings = mappings.Select(m =>
        {
            if (!Path.IsPathFullyQualified(m.From) || !Path.IsPathFullyQualified(m.To)) throw new InvalidDataException("Storage anchors must be absolute.");
            return new PathPrefixMapping(Path.GetFullPath(m.From).TrimEnd('\\','/'), Path.GetFullPath(m.To).TrimEnd('\\','/'));
        }).DistinctBy(m => m.From,StringComparer.OrdinalIgnoreCase).OrderByDescending(m => m.From.Length).ToArray();
    }
    internal string Map(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return path;
        foreach (var m in mappings)
            if (path.Equals(m.From,StringComparison.OrdinalIgnoreCase) || path.StartsWith(m.From+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) || path.StartsWith(m.From+Path.AltDirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) return m.To+path[m.From.Length..];
        return path;
    }
    internal string? Optional(string? path) => path == null ? null : Map(path);
    internal void Rewrite(JsonNode? node,bool manifest=false,bool pathArray=false)
    {
        if(node is JsonObject obj) foreach(var pair in obj.ToArray())
        {
            var key=pair.Key.ToLowerInvariant();
            if(key=="extra"&&!manifest) continue;
            var isPath=manifest || key is "path" or "directory" or "folder" or "folderprefix" or "thumbnailpath" or "subtitlepath" or "input" or "output";
            if(isPath && pair.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) obj[pair.Key]=Map(text);
            else Rewrite(pair.Value,manifest,manifest || key is "expandedfolders" or "ignoredpaths" or "inputs");
        }
        else if(node is JsonArray array) for(var i=0;i<array.Count;i++)
            if(pathArray && array[i] is JsonValue scalar && scalar.TryGetValue<string>(out var text)) array[i]=Map(text);
            else Rewrite(array[i],manifest,pathArray);
    }
}

public sealed partial class CatalogRepository
{
    private const string LocationId="686b9ac2-46b9-45f3-8758-a82f97ad9e23";
    private const string RelocationCommitId="686b9ac2-46b9-45f3-8758-a82f97ad9e24";
    internal Action<string>? RelocationCheckpoint { get; set; }
    private string RelocationJournal=>Path.Combine(Path.GetDirectoryName(DatabasePath)!,".relocation-pending");

    public void PreparePortableLocation(string applicationDirectory,bool portable)
    {
        lock(gate)
        {
            RecoverRelocation();
            var current=new PortableLocation(Path.GetDirectoryName(DatabasePath)!,Path.GetFullPath(applicationDirectory),Path.GetPathRoot(Path.GetFullPath(applicationDirectory))!);
            var previous=ReadLocation();
            var mappings=new List<PathPrefixMapping>();
            if(previous!=null)
            {
                mappings.Add(new(previous.Library,current.Library));
                if(portable) { mappings.Add(new(previous.Application,current.Application)); mappings.Add(new(previous.Volume,current.Volume)); }
            }
            // Catalogs from before the location was recorded never learned their first folder, so a later move kept
            // pointing into it (thumbnails of a takeover in the profile, opened again from a portable copy).
            if(previous?.ManagedPathsChecked!=true && FormerLibraryFolder(current.Library) is {} former) mappings.Add(new(former,current.Library));
            current=current with {ManagedPathsChecked=true};
            if(mappings.Any(m=>!LibraryCatalogs.SameDirectory(m.From,m.To))) ApplyRelocation(new(mappings),current);
            else WriteLocation(current,null);
        }
    }
    public void RelocateLibraryPaths(string previous,string current) => RelocateStoragePaths([new(previous,current)]);
    public void RelocateStoragePaths(IReadOnlyList<PathPrefixMapping> mappings)
    {
        lock(gate)
        {
            RecoverRelocation();
            var map=new StoragePathMap(mappings);
            var location=ReadLocation();
            ApplyRelocation(map,location==null?null:location with {Library=map.Map(location.Library)});
        }
    }
    // The legacy transition already publishes its complete staging folder atomically.
    internal void RemapManagedLibraryPaths(string previous,string current,CancellationToken token)=>ApplyDatabaseRelocation(new([new(previous,current)]),null,null,token);

    /// <summary>The earlier library folder that thumbnails still point into: the parent of their "thumbnails" folder,
    /// when that folder is gone and the same file lies in this library's "thumbnails" folder.</summary>
    private string? FormerLibraryFolder(string library)
    {
        var found=new Dictionary<string,(int Count,string File)>(StringComparer.OrdinalIgnoreCase);
        using(var read=Command("SELECT json_extract(document,'$.thumbnailPath') FROM media WHERE json_extract(document,'$.thumbnailPath') IS NOT NULL",null))
        using(var reader=read.ExecuteReader())
            while(reader.Read())
            {
                if(reader.GetValue(0) is not string path || !Path.IsPathFullyQualified(path)) continue;
                var folder=Path.GetDirectoryName(path);
                if(folder==null || !Path.GetFileName(folder).Equals("thumbnails",StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(folder) is not {} origin) continue;
                if(LibraryCatalogs.SameDirectory(origin,library)) continue;
                found[origin]=found.TryGetValue(origin,out var seen)?(seen.Count+1,seen.File):(1,Path.GetFileName(path));
            }
        if(found.Count==0) return null;
        var (former,(_,file))=found.MaxBy(pair=>pair.Value.Count);
        return !Directory.Exists(Path.Combine(former,"thumbnails")) && File.Exists(Path.Combine(library,"thumbnails",file)) ? former : null;
    }

    private PortableLocation? ReadLocation()
    {
        using var read=Command("SELECT document FROM ui_state WHERE id=$id",null,("$id",LocationId));
        return read.ExecuteScalar() is string value?JsonSerializer.Deserialize<PortableLocation>(value):null;
    }
    private void WriteLocation(PortableLocation location,SqliteTransaction? tx)
    {
        using var write=Command("INSERT INTO ui_state(id,document) VALUES($id,$document) ON CONFLICT(id) DO UPDATE SET document=excluded.document",tx,("$id",LocationId),("$document",JsonSerializer.Serialize(location))); write.ExecuteNonQuery();
    }
    private void RecoverRelocation()
    {
        StorageJournal.CleanupCompleted(Path.GetDirectoryName(DatabasePath)!,".relocation-pending");
        if(!Directory.Exists(RelocationJournal)) return;
        if(StorageJournal.RemoveUninitialized(RelocationJournal)) return;
        var journal=StorageJournal.Open(RelocationJournal,new Dictionary<string,string> {["library"]=Path.GetDirectoryName(DatabasePath)!});
        using var read=Command("SELECT document FROM ui_state WHERE id=$id",null,("$id",RelocationCommitId));
        if(read.ExecuteScalar() is string committed && committed==JsonSerializer.Serialize(journal.Id)) journal.Commit();
        else if(!journal.IsCommitting) journal.Discard();
        else throw new InvalidDataException("The relocation journal does not match the catalog commit.");
    }
    private void ApplyRelocation(StoragePathMap map,PortableLocation? location)
    {
        var library=Path.GetDirectoryName(DatabasePath)!;
        var journal=StorageJournal.Begin(RelocationJournal,new Dictionary<string,string> {["library"]=library},"relocation");
        journal.Checkpoint=RelocationCheckpoint;
        foreach(var path in ActiveManifests(library))
        {
            // Other JSON files lie in the library too (a takeover keeps its migration report of many MB beside the
            // catalog); only a queue or preview manifest of that size stops the relocation.
            if(new FileInfo(path).Length>MaxManifestBytes)
            {
                if(IsManifestFormat(LeadingFormat(path))) throw new InvalidDataException("An active manifest is too large.");
                continue;
            }
            var bytes=File.ReadAllBytes(path);
            JsonNode? node; try {node=JsonNode.Parse(bytes);} catch(JsonException) {continue;}
            if(node is not JsonObject obj) continue;
            var marker=obj.FirstOrDefault(p=>p.Key.Equals("format",StringComparison.OrdinalIgnoreCase)).Value;
            if(marker is not JsonValue value || !value.TryGetValue<string>(out var format) || !IsManifestFormat(format)) continue;
            map.Rewrite(obj,manifest:true);
            journal.AddFile("library",Path.GetRelativePath(library,path),JsonSerializer.SerializeToUtf8Bytes(obj),true);
        }
        CreateSafetyCopy(Path.Combine(library,"backups"),"paths-"+Guid.NewGuid().ToString("N")[..12]);
        RelocationCheckpoint?.Invoke("prepared");
        ApplyDatabaseRelocation(map,location,journal.Id,CancellationToken.None);
        RelocationCheckpoint?.Invoke("database-committed");
        journal.Commit();
        // Interrupted operations leave the journal. The token in the SQLite transaction
        // determines whether to discard preparation or finish publishing staged manifests.
    }
    private const int MaxManifestBytes=16*1024*1024;
    private static bool IsManifestFormat(string? format)=>format!=null && new[]{"flixypix-media-queue","flixypix-scene-previews"}.Any(f=>ProductIdentity.IsFormat(format,f));
    /// <summary>The top-level "format" of a JSON file, read from its first 64 KB; null when it is not there or not JSON.</summary>
    internal static string? LeadingFormat(string path)
    {
        var buffer=new byte[64*1024];int length;
        using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)) length=stream.ReadAtLeast(buffer,buffer.Length,throwOnEndOfStream:false);
        var reader=new Utf8JsonReader(buffer.AsSpan(0,length),isFinalBlock:false,state:default);
        try
        {
            if(!reader.Read() || reader.TokenType!=JsonTokenType.StartObject) return null;
            while(reader.Read() && reader.TokenType==JsonTokenType.PropertyName)
            {
                var isFormat=reader.GetString()!.Equals("format",StringComparison.OrdinalIgnoreCase);
                if(!reader.Read()) return null;
                if(isFormat) return reader.TokenType==JsonTokenType.String?reader.GetString():null;
                if(reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && !reader.TrySkip()) return null;
            }
        }
        catch(JsonException) {}
        return null;
    }
    private static IEnumerable<string> ActiveManifests(string directory)
    {
        foreach(var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if((entry.Attributes&FileAttributes.ReparsePoint)!=0) continue;
            if(entry is DirectoryInfo)
            {
                if(entry.Name=="backups" || entry.Name.StartsWith(".",StringComparison.Ordinal)) continue;
                foreach(var path in ActiveManifests(entry.FullName)) yield return path;
            }
            else if(entry.Extension.Equals(".json",StringComparison.OrdinalIgnoreCase)) yield return entry.FullName;
        }
    }
    private void ApplyDatabaseRelocation(StoragePathMap map,PortableLocation? location,string? commitId,CancellationToken token)
    {
        lock(gate)
        {
            ThrowIfDisposed(); var items=new List<MediaItem>();
            using(var select=Command("SELECT document FROM media ORDER BY id",null)) using(var reader=select.ExecuteReader()) while(reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var item=JsonSerializer.Deserialize<MediaItem>(reader.GetString(0),Json)!;
                var extra=new Dictionary<string,string?>(item.Extra);
                if(extra.TryGetValue("archivePath",out var archive)&&archive!=null) extra["archivePath"]=map.Map(archive);
                items.Add(item with {Path=map.Map(item.Path),ThumbnailPath=map.Optional(item.ThumbnailPath),SubtitlePath=map.Optional(item.SubtitlePath),Files=item.Files.Select(f=>f with {Path=map.Map(f.Path)}).ToList(),ContactSheet=item.ContactSheet is {} sheet?sheet with {SourcePath=map.Map(sheet.SourcePath)}:null,Extra=extra});
            }
            if(items.GroupBy(i=>PathKey(i.Path)).Any(g=>g.Count()>1)) throw new IOException("Relocation would merge different catalog paths.");
            using var tx=connection.BeginTransaction();
            foreach(var item in items)
            { using var reserve=Command("UPDATE media SET path_key=$key WHERE id=$id",tx,("$key","relocation:"+Guid.NewGuid()),("$id",item.Id.ToString())); reserve.ExecuteNonQuery(); }
            foreach(var item in items) {token.ThrowIfCancellationRequested();UpsertCore(item,tx);}
            foreach(var source in ReadDocuments<StashSourceProfile>("stash_sources",tx))
            {
                token.ThrowIfCancellationRequested();var directory=map.Map(source.SourceDirectory);
                var mappings=source.PathMappings.Select(mapping=>mapping with {TargetPrefix=map.Map(mapping.TargetPrefix)}).ToList();
                if(directory==source.SourceDirectory&&mappings.SequenceEqual(source.PathMappings))continue;
                var updated=source with {SourceDirectory=directory,PathMappings=mappings,Revision=checked(source.Revision+1)};
                ValidateStashSource(updated);SaveDocument("stash_sources",source.Id,updated,tx);
            }
            foreach(var table in new[]{"preferences","ui_state","saved_searches","scenes","bookmarks","media_assets"})
            {
                var rows=new List<(string Id,string Document)>();
                using(var select=Command("SELECT id,document FROM "+table,tx)) using(var reader=select.ExecuteReader()) while(reader.Read()) rows.Add((reader.GetString(0),reader.GetString(1)));
                foreach(var row in rows)
                {
                    var node=JsonNode.Parse(row.Document);map.Rewrite(node);
                    using var update=Command("UPDATE "+table+" SET document=$document WHERE id=$id",tx,("$document",node!.ToJsonString()),("$id",row.Id));update.ExecuteNonQuery();
                }
            }
            if(location!=null) WriteLocation(location,tx);
            if(commitId!=null)
            { using var marker=Command("INSERT INTO ui_state(id,document) VALUES($id,$document) ON CONFLICT(id) DO UPDATE SET document=excluded.document",tx,("$id",RelocationCommitId),("$document",JsonSerializer.Serialize(commitId)));marker.ExecuteNonQuery(); }
            token.ThrowIfCancellationRequested();tx.Commit();cachedMedia=null;
        }
    }
}
