using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;
public sealed record PathPrefixMapping(string From,string To);
public sealed record MediaPathChange(Guid MediaId,string Before,string After);
public sealed record PathMappingPlan(IReadOnlyList<PathPrefixMapping> Mappings,IReadOnlyList<MediaPathChange> Changes,IReadOnlyList<string> Conflicts);
public enum MediaAvailability { Available, Missing, TemporarilyOffline, Unavailable }
public sealed record MediaAvailabilityResult(Guid MediaId,string Path,MediaAvailability Status,long? Size,string? Detail=null);
public sealed partial class CatalogRepository
{
 public PathMappingPlan PlanPathMapping(IReadOnlyList<PathPrefixMapping> mappings,CancellationToken token=default)
 {
  ArgumentNullException.ThrowIfNull(mappings);
  var normalized=mappings.Select(m=>new PathPrefixMapping(Prefix(m.From),Prefix(m.To))).OrderByDescending(m=>m.From.Length).ToArray();
  if(normalized.Select(m=>m.From).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=normalized.Length)throw new ArgumentException("Each source prefix must occur only once.");
  lock(gate)
  {
   ThrowIfDisposed();var changes=new List<MediaPathChange>();var conflicts=new List<string>();
   foreach(var item in BackupMediaRows())
   {
    token.ThrowIfCancellationRequested();var path=Path.GetFullPath(item.Path);var match=normalized.FirstOrDefault(m=>Inside(path,m.From));if(match==null)continue;
    var target=Path.GetFullPath(match.To+path[match.From.Length..]);if(PathKey(path)==PathKey(target))continue;changes.Add(new(item.Id,item.Path,target));
   }
   foreach(var group in changes.GroupBy(c=>PathKey(c.After)))if(group.Count()>1)conflicts.Add("Multiple catalog items map to: "+group.First().After);
   var moving=changes.Select(c=>c.MediaId).ToHashSet();foreach(var change in changes)
   {var existing=GetMediaByPath(change.After);if(existing!=null&&!moving.Contains(existing.Id))conflicts.Add("Destination is already cataloged: "+change.After);}
   return new(normalized,changes,conflicts);
  }
 }
 public int ApplyPathMappingPlan(PathMappingPlan plan,CancellationToken token=default)
 {
  ArgumentNullException.ThrowIfNull(plan);if(plan.Conflicts.Count>0)throw new ArgumentException("Resolve path mapping conflicts before applying.");
  lock(gate)
  {
   ThrowIfDisposed();using var tx=connection.BeginTransaction();
   if(plan.Changes.Select(c=>c.MediaId).Distinct().Count()!=plan.Changes.Count||plan.Changes.Select(c=>PathKey(c.After)).Distinct().Count()!=plan.Changes.Count)throw new ArgumentException("Duplicate mapping identities or destinations.");
   using(var create=Command("CREATE TEMP TABLE mapping_originals(ordinal INTEGER PRIMARY KEY,id TEXT NOT NULL,document TEXT NOT NULL,target TEXT NOT NULL)",tx))create.ExecuteNonQuery();
   var ordinal=0;
   foreach(var change in plan.Changes)
   {
    token.ThrowIfCancellationRequested();using var command=Command("SELECT document FROM media WHERE id=$id",tx,("$id",change.MediaId.ToString()));var text=command.ExecuteScalar() as string??throw new InvalidOperationException("A planned media item was removed.");
    var item=JsonSerializer.Deserialize<MediaItem>(text,Json)!;if(!string.Equals(item.Path,change.Before,StringComparison.Ordinal))throw new InvalidOperationException("A catalog path changed after the preview.");
    if(!Path.IsPathFullyQualified(change.After))throw new ArgumentException("Mapped paths must be absolute.");
    using var stage=Command("INSERT INTO temp.mapping_originals VALUES($ordinal,$id,$document,$target)",tx,("$ordinal",ordinal++),("$id",item.Id.ToString()),("$document",text),("$target",Path.GetFullPath(change.After)));stage.ExecuteNonQuery();
   }
   // Reserve temporary path keys inside this transaction, allowing a previewed path swap.
   foreach(var change in plan.Changes){using var reserve=Command("UPDATE media SET path_key=$key WHERE id=$id",tx,("$key","migration-reservation:"+Guid.NewGuid()),("$id",change.MediaId.ToString()));reserve.ExecuteNonQuery();}
   using(var select=Command("SELECT document,target FROM temp.mapping_originals ORDER BY ordinal",tx))using(var reader=select.ExecuteReader())while(reader.Read())
   {
    token.ThrowIfCancellationRequested();var item=JsonSerializer.Deserialize<MediaItem>(reader.GetString(0),Json)!;var extra=new Dictionary<string,string?>(item.Extra);extra.TryAdd("migration.originalPath",item.Path);
    UpsertCore(item with {Path=reader.GetString(1),Extra=extra},tx);
   }
   using(var drop=Command("DROP TABLE temp.mapping_originals",tx))drop.ExecuteNonQuery();tx.Commit();return plan.Changes.Count;
  }
 }
 public IReadOnlyList<MediaAvailabilityResult> CheckMediaAvailability(int offset=0,int limit=120,IReadOnlyList<string>? offlineRoots=null,CancellationToken token=default)
 {
  var offline=(offlineRoots??new[]{@"U:\"}).Select(Prefix).ToArray();
  var items=QueryPage(new(){Offset=offset,Limit=limit},token).Items;var results=new List<MediaAvailabilityResult>();
  foreach(var item in items)
  {
   token.ThrowIfCancellationRequested();if(offline.Any(root=>Inside(item.Path,root))){results.Add(new(item.Id,item.Path,MediaAvailability.TemporarilyOffline,null,"Source root is configured offline; no filesystem lookup performed."));continue;}
   try{var info=new FileInfo(item.Path);info.Refresh();if(!info.Exists)
    {var root=Path.GetPathRoot(item.Path);results.Add(new(item.Id,item.Path,root!=null&&!Directory.Exists(root)?MediaAvailability.TemporarilyOffline:MediaAvailability.Missing,null));}
    else results.Add(new(item.Id,item.Path,MediaAvailability.Available,info.Length));}
   catch(Exception error)when(error is IOException or UnauthorizedAccessException){results.Add(new(item.Id,item.Path,MediaAvailability.Unavailable,null,error.GetType().Name));}
  }
  return results;
 }
 private static string Prefix(string path)
 {if(!Path.IsPathFullyQualified(path))throw new ArgumentException("Path mappings require absolute prefixes.");var full=Path.GetFullPath(path);return full.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);}
 private static bool Inside(string path,string prefix)=>path.Equals(prefix,StringComparison.OrdinalIgnoreCase)||path.StartsWith(prefix+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||path.StartsWith(prefix+Path.AltDirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
}
