using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed record StashPathPrefix(string SourcePrefix,string TargetPrefix);
public sealed record StashSourceProfile
{
 public Guid Id {get;init;}=Guid.NewGuid();
 public string Name {get;init;}="";
 public string ExportVersion {get;init;}=StashExport.Version;
 public required string SourceDirectory {get;init;}
 public List<StashPathPrefix> PathMappings {get;init;}=[];
 public long Revision {get;init;}
}
public sealed record StashMappedPath(string OriginalPath,string? TargetPath,int? MappingIndex);

/// <summary>Pure prefix mapping. It never probes a foreign drive, file, network address or media path.</summary>
public static class StashPaths
{
 public static StashMappedPath Map(string source,IReadOnlyList<StashPathPrefix> mappings)
 {
  ArgumentNullException.ThrowIfNull(mappings);var foreign=Foreign(source);Validate(mappings);
  var matches=mappings.Select((mapping,index)=>(Mapping:mapping,Index:index,Source:Foreign(mapping.SourcePrefix)))
   .Where(entry=>entry.Source.Windows==foreign.Windows&&Contains(entry.Source.Path,foreign.Path,foreign.Windows)).OrderByDescending(entry=>entry.Source.Path.Length).ToArray();
  if(matches.Length==0)return new(source,null,null);
  var chosen=matches[0];var suffix=foreign.Path[chosen.Source.Path.Length..].TrimStart('/');
  var target=Path.GetFullPath(chosen.Mapping.TargetPrefix);
  foreach(var segment in suffix.Split('/',StringSplitOptions.RemoveEmptyEntries))
  {
   ValidateTargetSegment(segment);target=Path.Combine(target,segment);
  }
  target=Path.GetFullPath(target);var root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(chosen.Mapping.TargetPrefix));
  if(!ContainsLocal(root,target))throw new ArgumentException("Mapped Stash path leaves the selected destination.");
  return new(source,target,chosen.Index);
 }
 /// <summary>The entry name of an archive member: Stash stores it as the archive path, a separator of the source system and the
 /// slash-separated path inside the archive. Null when the member path does not lie inside the archive path.</summary>
 public static string? ArchiveEntry(string memberPath,string archivePath)
 {
  var member=Foreign(memberPath);var archive=Foreign(archivePath);
  if(member.Windows!=archive.Windows||member.Path.Length<=archive.Path.Length+1||member.Path[archive.Path.Length]!='/')return null;
  // The archive part follows the source system's rules; names inside the archive are compared exactly.
  if(!member.Path.StartsWith(archive.Path,member.Windows?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))return null;
  var entry=member.Path[(archive.Path.Length+1)..];
  return entry.Split('/').Any(segment=>segment.Length==0)?null:entry;
 }
 internal static void Validate(IReadOnlyList<StashPathPrefix> mappings)
 {
  if(mappings.Count>128)throw new ArgumentException("Too many Stash path prefix mappings.");
  var seen=new HashSet<string>(StringComparer.Ordinal);
  foreach(var mapping in mappings)
  {
   if(mapping==null)throw new ArgumentException("Null Stash path mapping.");var source=Foreign(mapping.SourcePrefix);
   if(!seen.Add((source.Windows?"win:"+source.Path.ToUpperInvariant():"unix:"+source.Path)))throw new ArgumentException("Duplicate Stash source prefix.");
   LocalDirectory(mapping.TargetPrefix);
  }
 }
 internal static void LocalDirectory(string path)
 {
  if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)||path.Any(char.IsControl))throw new ArgumentException("Choose an absolute local directory for the Stash source or mapping.");
  if(path.StartsWith(@"\\?\",StringComparison.Ordinal)||path.StartsWith(@"\\.\",StringComparison.Ordinal))throw new ArgumentException("Stash mappings cannot target a Windows device namespace.");
  var full=Path.GetFullPath(path);var root=Path.GetPathRoot(full)!;
  foreach(var segment in full[root.Length..].Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar))if(segment.Length>0)ValidateTargetSegment(segment);
  // Reject paths whose explicit components would be silently interpreted as parent traversal.
  foreach(var segment in path.Replace('\\','/').Split('/'))if(segment is "." or "..")throw new ArgumentException("Stash local paths cannot contain dot segments.");
 }
 private static void ValidateTargetSegment(string segment)
 {
  var stem=segment.Split('.')[0].ToUpperInvariant();
  if(segment is "." or ".."||segment.EndsWith('.')||segment.EndsWith(' ')||segment.IndexOfAny(Path.GetInvalidFileNameChars())>=0||
   stem is "CON" or "PRN" or "AUX" or "NUL"||(stem.StartsWith("COM",StringComparison.Ordinal)||stem.StartsWith("LPT",StringComparison.Ordinal))&&stem.Length==4&&"123456789\u00b9\u00b2\u00b3".Contains(stem[3]))throw new ArgumentException("A mapped Stash path contains an invalid Windows component.");
 }
 private static bool ContainsLocal(string root,string path)=>root.Equals(path,StringComparison.OrdinalIgnoreCase)||path.StartsWith(Path.EndsInDirectorySeparator(root)?root:root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
 private static bool Contains(string root,string path,bool windows)
 {var comparison=windows?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;return root.Equals(path,comparison)||path.StartsWith(root.EndsWith('/')?root:root+"/",comparison);}
 internal static (string Path,bool Windows) Foreign(string value)
 {
  if(string.IsNullOrWhiteSpace(value)||value.Length>32768||value.Any(char.IsControl))throw new ArgumentException("Invalid Stash source path.");
  if(value.StartsWith('\\')&&!value.StartsWith(@"\\",StringComparison.Ordinal))throw new ArgumentException("A Windows Stash path must include its drive or UNC share.");
  var path=value.Replace('\\','/');var drive=path.Length>=3&&char.IsAsciiLetter(path[0])&&path[1]==':'&&path[2]=='/';var unc=path.StartsWith("//",StringComparison.Ordinal);
  var windows=drive||unc;
  if(!drive&&!unc&&!path.StartsWith('/'))throw new ArgumentException("Stash paths must be absolute Unix, Windows-drive or UNC paths.");
  if(unc&&(path.StartsWith("///",StringComparison.Ordinal)||path.StartsWith("//?/",StringComparison.Ordinal)||path.StartsWith("//./",StringComparison.Ordinal)||path.Split('/',StringSplitOptions.RemoveEmptyEntries).Length<2))throw new ArgumentException("Invalid Stash UNC path.");
  if(path.Split('/').Any(segment=>segment is "." or ".."))throw new ArgumentException("Stash source paths cannot contain dot segments.");
  if(path.IndexOf("//",unc?2:0,StringComparison.Ordinal)>=0)throw new ArgumentException("Stash source paths cannot contain empty components.");
  path=path.TrimEnd('/');if(path.Length==0)path="/";else if(drive&&path.Length==2)path+="/";
  return(path,windows);
 }
}

public sealed partial class CatalogRepository
{
 private void InitializeStashSources(SqliteTransaction transaction)
 {using var command=Command("CREATE TABLE IF NOT EXISTS stash_sources(id TEXT PRIMARY KEY,document TEXT NOT NULL)",transaction);command.ExecuteNonQuery();}
 internal static void ValidateStashSource(StashSourceProfile profile)
 {
  if(profile==null||profile.Id==Guid.Empty||string.IsNullOrWhiteSpace(profile.Name)||profile.Name.Length>256||profile.Name.Any(char.IsControl)||profile.ExportVersion!=StashExport.Version||profile.Revision<0||profile.PathMappings==null)throw new ArgumentException("Invalid Stash import source profile.");
  StashPaths.LocalDirectory(profile.SourceDirectory);StashPaths.Validate(profile.PathMappings);
 }
 public IReadOnlyList<StashSourceProfile> GetStashSources()
 {lock(gate){ThrowIfDisposed();return ReadDocuments<StashSourceProfile>("stash_sources").OrderBy(profile=>profile.Name,StringComparer.CurrentCultureIgnoreCase).ThenBy(profile=>profile.Id).ToArray();}}
 public StashSourceProfile? GetStashSource(Guid id)
 {lock(gate){ThrowIfDisposed();return ReadEntity<StashSourceProfile>("stash_sources",id);}}
 /// <summary>Explicit create/update. Null expectedRevision means create; updates require the last observed revision.</summary>
 public StashSourceProfile SaveStashSource(StashSourceProfile profile,long? expectedRevision=null)
 {
  ValidateStashSource(profile);
  lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();var current=ReadEntity<StashSourceProfile>("stash_sources",profile.Id,transaction);
   if(current==null?expectedRevision!=null:expectedRevision!=current.Revision)throw new InvalidOperationException("The import source changed. Reload it before saving.");
   var saved=profile with {Name=profile.Name.Trim(),SourceDirectory=Path.GetFullPath(profile.SourceDirectory),PathMappings=profile.PathMappings.ToList(),Revision=checked((current?.Revision??0)+1)};
   SaveDocument("stash_sources",saved.Id,saved,transaction);transaction.Commit();return saved;
  }
 }
}
