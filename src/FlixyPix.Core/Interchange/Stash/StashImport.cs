using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed record StashImportCandidate(StashTargetReference Target,string Label);
/// <summary>What one source record would do. Previewing never writes; Fields compare the source with the bound or single matching local target.</summary>
public sealed record StashImportPreviewRow(StashImportDraft Draft,StashIdentityProposal Identity,StashBinding? Binding,
 IReadOnlyList<StashImportCandidate> LocalMatches,IReadOnlyList<StashFieldDiff> Fields)
{
 public bool Supported=>CatalogRepository.StashImportSupported(Draft.Kind);
}
/// <summary>One selected record. Without Target the bound target is updated, or a new one is created when there is none.</summary>
public sealed record StashImportItem(long SourceOrdinal)
{
 /// <summary>The identity confirmed in the preview; null registers a new one (refused when the preview found a clear match).</summary>
 public Guid? IdentityId {get;init;}
 public long? ExpectedIdentityRevision {get;init;}
 /// <summary>An existing local target to bind, for example a medium already at the mapped path.</summary>
 public StashTargetReference? Target {get;init;}
 /// <summary>Creates a new target although an earlier binding exists but its target was removed.</summary>
 public bool CreateTarget {get;init;}
 /// <summary>Explicit decisions; fields without one keep their local value.</summary>
 public IReadOnlyList<StashFieldChoice> Fields {get;init;}=[];
 /// <summary>Directory name for a new performer or studio, for example to separate two source entries of the same name.</summary>
 public string? TargetName {get;init;}
}
public sealed record StashImportRequest(Guid SourceId,long ExpectedSourceRevision,string PerformerCategory,string StudioCategory,IReadOnlyList<StashImportItem> Items);
public sealed record StashImportResult(IReadOnlyList<StashBinding> Bindings,int Created,int Updated,int Unchanged,IReadOnlyList<string> Warnings);

public sealed partial class CatalogRepository
{
 private const int MaximumStashImportItems=256;
 internal static bool StashImportSupported(StashEntityKind kind)=>kind is StashEntityKind.Scene or StashEntityKind.Image or StashEntityKind.Performer or StashEntityKind.Studio or StashEntityKind.Tag;
 private sealed record StashImportRecord(StashSourceDocument Document,StashImportDraft Draft);
 private sealed record StashPlannedBinding(StashIdentity Identity,long? ExpectedIdentityRevision,StashSourceDocument Document,StashBinding? Current,StashTargetReference Target,
  IReadOnlyList<StashFieldBaseline> Fields,bool RetargetConfirmed);

 public IReadOnlyList<StashImportPreviewRow> PreviewStashImport(StashExportSnapshot snapshot,Guid sourceId,string performerCategory,string studioCategory,int offset,int limit=24,CancellationToken token=default)
 {
  ArgumentNullException.ThrowIfNull(snapshot);var categories=StashCategories(performerCategory,studioCategory);
  var profile=GetStashSource(sourceId)??throw new ArgumentException("Unknown Stash source.");
  var drafts=snapshot.ReadDraftPage(profile,offset,limit,null,token);
  var records=drafts.Select(draft=>(Document:snapshot.ReadDocument(draft.SourceOrdinal)!,Draft:draft)).ToArray();
  var dependencies=ReadStashDependencies(snapshot,records.Select(record=>record.Draft),token);
  lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();
   var source=ReadEntity<StashSourceProfile>("stash_sources",sourceId,transaction)??throw new ArgumentException("Unknown Stash source.");
   var result=new List<StashImportPreviewRow>();
   foreach(var(document,draft)in records)
   {
    token.ThrowIfCancellationRequested();var identity=ResolveStashIdentity(source,document,transaction,token);
    var binding=identity is {State:StashIdentityState.Matched,Candidates:[var only]}?ReadEntity<StashBinding>("stash_bindings",only.Id,transaction):null;
    var lookup=new StashTargetLookup(this,transaction);if(binding!=null)binding=ObserveStashBinding(binding,draft.Kind,lookup);
    var matches=StashImportSupported(draft.Kind)?StashLocalMatches(draft,categories,transaction):[];
    var compared=binding is {Detached:false}?binding.Target:matches.Count==1?matches[0].Target:null;
    IReadOnlyList<StashFieldDiff> fields=[];
    if(compared!=null&&StashImportSupported(draft.Kind)&&compared.Kind!=StashTargetKind.ProvenanceOnly)
    {
     try{fields=StashFieldConflicts.Compare(StashObservations(document,draft,compared,categories,dependencies,source,transaction),binding?.Fields);}
     catch(Exception error)when(error is ArgumentException or InvalidOperationException){fields=[];}
    }
    result.Add(new(draft,identity,binding,matches,fields));
   }
   transaction.Commit();return result;
  }
 }

 /// <summary>Applies the selected records in one transaction: target writes, source identities, raw provenance and field decisions
 /// are published together or not at all. The source folder must still match the snapshot.</summary>
 public async Task<StashImportResult> ApplyStashImportAsync(StashExportSnapshot snapshot,StashImportRequest request,CancellationToken token=default)
 {
  ArgumentNullException.ThrowIfNull(snapshot);ArgumentNullException.ThrowIfNull(request);ArgumentNullException.ThrowIfNull(request.Items);
  var categories=StashCategories(request.PerformerCategory,request.StudioCategory);
  if(request.Items.Count is <1 or >MaximumStashImportItems||request.Items.Any(item=>item==null||item.Fields==null)||request.Items.Select(item=>item.SourceOrdinal).Distinct().Count()!=request.Items.Count)
   throw new ArgumentException("Choose between 1 and 256 distinct Stash records.");
  await snapshot.VerifySourceAsync(token).ConfigureAwait(false);
  var profile=GetStashSource(request.SourceId)??throw new ArgumentException("Unknown Stash source.");
  if(profile.Revision!=request.ExpectedSourceRevision)throw new InvalidOperationException("The Stash source changed after preview.");
  var records=new List<StashImportRecord>();
  foreach(var item in request.Items)
  {
   token.ThrowIfCancellationRequested();
   var read=snapshot.ReadDraft(profile,item.SourceOrdinal,File.Exists,token)??throw new ArgumentException("The selected Stash record is not part of this export.");
   records.Add(new(read.Document,read.Draft));
  }
  var dependencies=ReadStashDependencies(snapshot,records.Select(record=>record.Draft),token);
  return await Task.Run(()=>ApplyStashImport(records,request,categories,dependencies,token),token).ConfigureAwait(false);
 }

 private StashImportResult ApplyStashImport(IReadOnlyList<StashImportRecord> records,StashImportRequest request,(string Performer,string Studio) categories,
  IReadOnlyDictionary<long,StashSourceDocument> dependencies,CancellationToken token)
 {
  lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();
   var source=ReadEntity<StashSourceProfile>("stash_sources",request.SourceId,transaction)??throw new ArgumentException("Unknown Stash source.");
   if(source.Revision!=request.ExpectedSourceRevision)throw new InvalidOperationException("The Stash source changed after preview.");
   var items=request.Items.ToDictionary(item=>item.SourceOrdinal);var warnings=new List<string>();
   foreach(var record in records)
   {
    if(!StashImportSupported(record.Draft.Kind))throw new ArgumentException(T("This kind of Stash record cannot be imported yet: {0}",record.Draft.RelativePath));
    if(record.Draft.HasErrors)throw new ArgumentException(T("The Stash record needs corrections: {0}",record.Draft.RelativePath+" · "+record.Draft.Issues.First(issue=>issue.Severity==StashIssueSeverity.Error).Message));
   }
   // Identities first: every later step refers to one stable source identity per record.
   var identities=new Dictionary<long,(StashIdentity Identity,long? Expected,StashBinding? Binding)>();var seen=new HashSet<Guid>();
   foreach(var record in records)
   {
    token.ThrowIfCancellationRequested();var item=items[record.Document.Ordinal];
    var proposal=ResolveStashIdentity(source,record.Document,transaction,token);
    StashIdentity identity;long? expected;
    if(item.IdentityId is {} chosen)
    {
     var candidate=proposal.Candidates.FirstOrDefault(c=>c.Id==chosen)??throw new InvalidOperationException(T("The Stash identities changed. Refresh the preview."));
     if(item.ExpectedIdentityRevision!=candidate.Revision)throw new InvalidOperationException(T("The Stash identities changed. Refresh the preview."));
     var current=ReadEntity<StashIdentity>("stash_identities",chosen,transaction)!;
     identity=current with {Keys=StashExport.IdentityKeys(record.Document).ToList()};expected=current.Revision;
    }
    else
    {
     if(proposal.State is StashIdentityState.Matched or StashIdentityState.Invalid)throw new InvalidOperationException(T("The Stash identities changed. Refresh the preview."));
     identity=new StashIdentity{SourceId=source.Id,Kind=record.Document.Kind,Keys=StashExport.IdentityKeys(record.Document).ToList()};expected=null;
    }
    if(!seen.Add(identity.Id))throw new ArgumentException("Two selected Stash records refer to the same source identity.");
    var binding=expected==null?null:ReadEntity<StashBinding>("stash_bindings",identity.Id,transaction);
    if(binding!=null)binding=ObserveStashBinding(binding,record.Document.Kind,new(this,transaction));
    identities[record.Document.Ordinal]=(identity,expected,binding);
   }
   var planned=new List<StashPlannedBinding>();int created=0,updated=0,unchanged=0;
   // Directory entries before media, so media in the same selection can refer to them.
   var selectedDirectory=new Dictionary<long,TagKey>();
   foreach(var record in records.Where(r=>r.Draft.Kind is StashEntityKind.Performer or StashEntityKind.Studio))
   {
    token.ThrowIfCancellationRequested();var item=items[record.Document.Ordinal];var(identity,expected,binding)=identities[record.Document.Ordinal];
    var(target,isNew,retarget)=StashTarget(record,item,binding,categories,transaction);
    var key=target.Tag!;
    if(selectedDirectory.Values.Any(other=>SameHierarchyKey(other,key)))throw new ArgumentException(T("Two selected Stash entries would get the same directory name: {0}",key.Value));
    selectedDirectory[record.Document.Ordinal]=key;
    if(isNew)CreateStashDirectoryEntry(record,key,transaction);
    var plan=StashDecide(record,item,binding,target,isNew,categories,selectedDirectory,dependencies,source,transaction,ref created,ref updated,ref unchanged);
    planned.Add(new(identity,expected,record.Document,binding,target,plan,retarget));
   }
   foreach(var record in records.Where(r=>r.Draft.Kind is StashEntityKind.Scene or StashEntityKind.Image))
   {
    token.ThrowIfCancellationRequested();var item=items[record.Document.Ordinal];var(identity,expected,binding)=identities[record.Document.Ordinal];
    RequireStashDependencies(record.Draft,selectedDirectory,dependencies,source,transaction);
    var(target,isNew,retarget)=StashTarget(record,item,binding,categories,transaction);
    if(isNew)
    {
     target=new(target.Kind,CreateStashMedia(record,categories,selectedDirectory,dependencies,source,transaction,warnings).Id);
    }
    var plan=StashDecide(record,item,binding,target,isNew,categories,selectedDirectory,dependencies,source,transaction,ref created,ref updated,ref unchanged);
    planned.Add(new(identity,expected,record.Document,binding,target,plan,retarget));
   }
   // Plain tags have no directory entry: they exist through their use on media. Their source record is kept as provenance;
   // aliases and descriptions of a Stash tag therefore remain only there.
   var provenanceOnly=new StashTargetReference(StashTargetKind.ProvenanceOnly);
   foreach(var record in records.Where(r=>r.Draft.Kind==StashEntityKind.Tag))
   {
    token.ThrowIfCancellationRequested();var item=items[record.Document.Ordinal];var(identity,expected,binding)=identities[record.Document.Ordinal];
    if(item.Fields.Count>0||item.Target!=null||item.TargetName!=null||item.CreateTarget)throw new ArgumentException(T("Plain tags have no editable directory fields: {0}",record.Draft.Name));
    if(record.Draft.Directory is {} tag&&(tag.Aliases.Count>0||tag.Description!=null))warnings.Add(T("Aliases and the description of a tag are kept only with its source record: {0}",record.Draft.Name));
    if(binding==null)created++;else if(binding.Target==provenanceOnly&&!binding.Detached)unchanged++;else updated++;
    planned.Add(new(identity,expected,record.Document,binding,provenanceOnly,binding?.Fields??[],binding!=null&&binding.Target!=provenanceOnly));
   }
   // Identity, raw provenance and the binding are written after every target, against one fresh lookup.
   var lookup=new StashTargetLookup(this,transaction);var bindings=new List<StashBinding>();
   foreach(var plan in planned)
   {
    token.ThrowIfCancellationRequested();
    var identity=SaveStashIdentity(plan.Identity,plan.ExpectedIdentityRevision,transaction);
    var provenance=SaveStashProvenance(identity.Id,plan.Document,transaction,token);
    var binding=new StashBinding{Id=identity.Id,ProvenanceId=provenance.Id,Target=plan.Target,Fields=plan.Fields.ToList()};
    bindings.Add(SaveStashBinding(binding,plan.Current==null?null:ReadEntity<StashBinding>("stash_bindings",identity.Id,transaction)?.Revision,plan.RetargetConfirmed,transaction,lookup));
   }
   token.ThrowIfCancellationRequested();transaction.Commit();
   return new(bindings,created,updated,unchanged,warnings);
  }
 }

 private static (string Performer,string Studio) StashCategories(string performer,string studio)
 {
  static string Check(string? value)=>string.IsNullOrWhiteSpace(value)||value.Length>256||value.Any(char.IsControl)?throw new ArgumentException("Choose a category for performers and studios."):value.Trim();
  var result=(Check(performer),Check(studio));if(Same(result.Item1,result.Item2))throw new ArgumentException("Performers and studios need separate categories.");return result;
 }

 /// <summary>Referenced performer, studio and tag records, read before the catalog lock is taken.</summary>
 private static IReadOnlyDictionary<long,StashSourceDocument> ReadStashDependencies(StashExportSnapshot snapshot,IEnumerable<StashImportDraft> drafts,CancellationToken token)
 {
  var result=new Dictionary<long,StashSourceDocument>();
  foreach(var reference in drafts.SelectMany(draft=>draft.References))
  {
   token.ThrowIfCancellationRequested();
   if(reference.TargetOrdinal is not {} ordinal||reference.Reference.Kind is not (StashEntityKind.Performer or StashEntityKind.Studio or StashEntityKind.Tag)||result.ContainsKey(ordinal))continue;
   if(result.Count>=4096)throw new ArgumentException("The selection refers to too many source records.");
   result[ordinal]=snapshot.ReadDocument(ordinal)??throw new InvalidDataException("A referenced Stash record is missing from the snapshot.");
  }
  return result;
 }

 private static string StashName(StashSourceDocument document)=>document.Document is {} root&&root.TryGetProperty("name",out var name)&&name.ValueKind==JsonValueKind.String?name.GetString()!.Trim():"";

 /// <summary>The local directory key of a referenced performer or studio: selected in this import, or bound earlier.</summary>
 private TagKey? StashDirectoryKey(long ordinal,IReadOnlyDictionary<long,TagKey> selected,IReadOnlyDictionary<long,StashSourceDocument> dependencies,StashSourceProfile source,SqliteTransaction transaction)
 {
  if(selected.TryGetValue(ordinal,out var key))return key;
  if(!dependencies.TryGetValue(ordinal,out var document))return null;
  var identity=ResolveStashIdentity(source,document,transaction,CancellationToken.None);
  if(identity is not {State:StashIdentityState.Matched,Candidates:[var only]})return null;
  var binding=ReadEntity<StashBinding>("stash_bindings",only.Id,transaction);
  return binding is {Detached:false,Target:{Kind:StashTargetKind.DirectoryEntry,Tag:{} bound}}&&new StashTargetLookup(this,transaction).Exists(binding.Target,document.Kind)?bound:null;
 }

 private void RequireStashDependencies(StashImportDraft draft,IReadOnlyDictionary<long,TagKey> selected,IReadOnlyDictionary<long,StashSourceDocument> dependencies,StashSourceProfile source,SqliteTransaction transaction)
 {
  foreach(var reference in draft.References.Where(r=>r.Reference.Relation is "performer" or "studio"))
   if(StashDirectoryKey(reference.TargetOrdinal!.Value,selected,dependencies,source,transaction)==null)
    throw MissingStashDependency(reference.Reference.Relation,reference.Reference.Key["name:".Length..]);
 }

 private static ArgumentException MissingStashDependency(string relation,string name)=>new(relation=="performer"
  ?T("Select the referenced performer too, or import it first: {0}",name):T("Select the referenced studio too, or import it first: {0}",name));

 /// <summary>The target of one record: explicit choice, the existing binding, or a new target.</summary>
 private (StashTargetReference Target,bool IsNew,bool Retarget) StashTarget(StashImportRecord record,StashImportItem item,StashBinding? binding,(string Performer,string Studio) categories,SqliteTransaction transaction)
 {
  var kind=record.Draft.Kind switch{StashEntityKind.Scene=>StashTargetKind.Video,StashEntityKind.Image=>StashTargetKind.Image,_=>StashTargetKind.DirectoryEntry};
  if(item.Target is {} chosen)
  {
   if(chosen.Kind!=kind||item.CreateTarget)throw new ArgumentException("The chosen local target does not fit this Stash record.");
   if(item.TargetName!=null)throw new ArgumentException("A directory name is only used for a new entry.");
   return(chosen.Tag is {} tag?chosen with {Tag=NormalizeHierarchyKey(tag)}:chosen,false,true);
  }
  if(binding is {Detached:false}&&!item.CreateTarget)
  {if(item.TargetName!=null)throw new ArgumentException("A directory name is only used for a new entry.");return(binding.Target,false,false);}
  if(binding!=null&&!item.CreateTarget)throw new InvalidOperationException(T("The local target of this Stash record was removed. Choose a target or create a new one: {0}",record.Draft.Name));
  if(kind==StashTargetKind.DirectoryEntry)
  {
   var name=(item.TargetName??record.Draft.Name).Trim();
   var key=NormalizeHierarchyKey(new TagKey(record.Draft.Kind==StashEntityKind.Performer?categories.Performer:categories.Studio,name));
   if(DirectoryKeyExists(key,transaction))throw new InvalidOperationException(T("A local directory entry already has this name. Choose it as the target or enter another name: {0}",name));
   return(new(kind,Tag:key),true,binding!=null);
  }
  if(item.TargetName!=null)throw new ArgumentException("A directory name is only used for a new entry.");
  var path=StashMainPath(record.Draft)??throw new ArgumentException(T("The Stash record has no mapped local file: {0}",record.Draft.RelativePath));
  using(var existing=Command("SELECT id FROM media WHERE path_key=$path",transaction,("$path",PathKey(path))))
   if(existing.ExecuteScalar()!=null)throw new InvalidOperationException(T("A local medium already uses this file. Choose it as the target: {0}",path));
  return(new(kind,Guid.Empty),true,binding!=null);
 }

 private static string? StashMainPath(StashImportDraft draft)=>draft.Media?.Files.FirstOrDefault(file=>file.TargetPath!=null&&file.ArchiveSource==null)?.TargetPath;

 private bool DirectoryKeyExists(TagKey key,SqliteTransaction transaction)
 {
  var preferences=ReadPreferences(transaction);
  if(preferences.TagVocabulary.Any(entry=>Same(entry.Tag,key.Value)&&Same(entry.Category,key.Category)))return true;
  using(var command=Command("SELECT 1 FROM media_tags WHERE tag=$tag COLLATE EFP_ORDINAL AND category=$category COLLATE EFP_ORDINAL LIMIT 1",transaction,("$tag",key.Value),("$category",key.Category)))
   if(command.ExecuteScalar()!=null)return true;
  using(var any=Command("SELECT 1 FROM media_tags WHERE tag=$tag COLLATE NOCASE AND category=$category COLLATE NOCASE LIMIT 1",transaction,("$tag",key.Value),("$category",key.Category)))
   if(any.ExecuteScalar()!=null)return true;
  return ReadTagHierarchy(transaction).Edges.Any(edge=>SameHierarchyKey(edge.Parent,key)||SameHierarchyKey(edge.Child,key));
 }

 private IReadOnlyList<StashImportCandidate> StashLocalMatches(StashImportDraft draft,(string Performer,string Studio) categories,SqliteTransaction transaction)
 {
  var result=new List<StashImportCandidate>();
  if(draft.Media is {} media)
  {
   foreach(var file in media.Files.Where(file=>file.TargetPath!=null))
   {
    using var command=Command("SELECT document FROM media WHERE path_key=$path",transaction,("$path",PathKey(file.TargetPath!)));
    if(command.ExecuteScalar() is string json&&JsonSerializer.Deserialize<MediaItem>(json,Json) is {} item&&result.All(r=>r.Target.Id!=item.Id))
     result.Add(new(new(item.Kind==MediaKind.Video?StashTargetKind.Video:StashTargetKind.Image,item.Id),item.Title.Length>0?item.Title:item.Path));
   }
  }
  else if(draft.Kind is StashEntityKind.Performer or StashEntityKind.Studio)
  {
   var key=NormalizeHierarchyKey(new TagKey(draft.Kind==StashEntityKind.Performer?categories.Performer:categories.Studio,draft.Name));
   if(DirectoryKeyExists(key,transaction))result.Add(new(new(StashTargetKind.DirectoryEntry,Tag:key),key.Category+": "+key.Value));
  }
  return result;
 }

 private void CreateStashDirectoryEntry(StashImportRecord record,TagKey key,SqliteTransaction transaction)
 {
  var preferences=ReadPreferences(transaction);var role=record.Draft.Kind==StashEntityKind.Performer?TagRole.Performer:TagRole.Studio;
  var roles=preferences.TagCategoryRoles;
  if(TagRoles.Of(preferences,key.Category)!=role)
  {
   var used=preferences.TagVocabulary.Any(entry=>Same(entry.Category,key.Category));
   if(!used){using var command=Command("SELECT 1 FROM media_tags WHERE category=$category COLLATE NOCASE LIMIT 1",transaction,("$category",key.Category));used=command.ExecuteScalar()!=null;}
   if(used)throw new InvalidOperationException(T("The category is already used with another role: {0}",key.Category));
   roles=TagRoles.Set(roles,key.Category,role);
  }
  var directory=record.Draft.Directory!;
  var entry=new TagVocabularyEntry(key.Value,key.Category,directory.Favorite??false,null,directory.Aliases.Count>0?directory.Aliases.ToList():null,directory.Description,directory.Profile);
  var updated=preferences with {TagVocabulary=preferences.TagVocabulary.Append(entry).ToList(),TagCategoryRoles=roles};
  ValidatePreferences(updated);SaveDocument("preferences",Guid.Empty,updated,transaction);
 }

 private MediaItem CreateStashMedia(StashImportRecord record,(string Performer,string Studio) categories,IReadOnlyDictionary<long,TagKey> selected,
  IReadOnlyDictionary<long,StashSourceDocument> dependencies,StashSourceProfile source,SqliteTransaction transaction,List<string> warnings)
 {
  var media=record.Draft.Media!;var file=media.Files.First(f=>f.TargetPath!=null&&f.ArchiveSource==null);
  if(media.Files.Count>1)warnings.Add(T("Further files stay proposals and are not added as versions yet: {0}",record.Draft.Name));
  var(plain,categorized)=StashSourceTags(record.Draft,selected,dependencies,source,transaction);
  var item=new MediaItem
  {
   Path=file.TargetPath!,Kind=media.Kind,Title=media.Title??Path.GetFileNameWithoutExtension(file.TargetPath!),Description=media.Description,Rating=Math.Clamp(media.Rating??0,0,10),
   ReleaseDate=media.ReleaseDate,Tags=plain.ToList(),TagCategories=categorized,FileSize=file.Bytes,Width=file.Width is >0?file.Width:null,Height=file.Height is >0?file.Height:null,
   DurationSeconds=file.DurationSeconds,Technical=file.Technical,ReviewState=media.InitialReviewState,
  };
  var saved=UpsertCore(item,transaction);
  if(media.Kind==MediaKind.Video)AddStashMarkers(saved.Id,media.Markers,transaction);
  return saved;
 }

 /// <param name="preview">Categories for a performer or studio that is neither bound nor selected yet; only the preview shows such a prospective name.</param>
 private (IReadOnlyList<string> Plain,Dictionary<string,List<string>> Categorized) StashSourceTags(StashImportDraft draft,IReadOnlyDictionary<long,TagKey> selected,
  IReadOnlyDictionary<long,StashSourceDocument> dependencies,StashSourceProfile source,SqliteTransaction transaction,(string Performer,string Studio)? preview=null)
 {
  var plain=draft.Media!.Tags.Where(t=>!string.IsNullOrWhiteSpace(t)).Select(t=>t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
  var categorized=new Dictionary<string,List<string>>(StringComparer.OrdinalIgnoreCase);
  foreach(var reference in draft.References.Where(r=>r.Reference.Relation is "performer" or "studio"))
  {
   var name=reference.Reference.Key["name:".Length..];
   var key=StashDirectoryKey(reference.TargetOrdinal!.Value,selected,dependencies,source,transaction)
    ??(preview is {} categories?new TagKey(reference.Reference.Relation=="performer"?categories.Performer:categories.Studio,name.Trim())
    :throw MissingStashDependency(reference.Reference.Relation,name));
   if(!categorized.TryGetValue(key.Category,out var list))categorized[key.Category]=list=[];
   if(!list.Contains(key.Value,StringComparer.OrdinalIgnoreCase))list.Add(key.Value);
  }
  return(plain,categorized);
 }

 private static JsonElement StashTagValue(IEnumerable<string> plain,IReadOnlyDictionary<string,List<string>> categorized)
 {
  var pairs=plain.Select(t=>new[]{"",t}).Concat(categorized.SelectMany(pair=>pair.Value.Select(t=>new[]{pair.Key,t})))
   .DistinctBy(pair=>(pair[0].ToUpperInvariant(),pair[1].ToUpperInvariant())).OrderBy(pair=>pair[0],StringComparer.OrdinalIgnoreCase).ThenBy(pair=>pair[1],StringComparer.OrdinalIgnoreCase).ToArray();
  return JsonSerializer.SerializeToElement(pairs,Json);
 }

 private sealed record StashMarkerValue(string Title,double Start,double? End,List<string> Tags);
 private static List<StashMarkerValue> SortedMarkers(IEnumerable<StashMarkerValue> markers)=>markers.Select(m=>m with {Tags=m.Tags.Order(StringComparer.OrdinalIgnoreCase).ToList()})
  .OrderBy(m=>m.Start).ThenBy(m=>m.End??-1).ThenBy(m=>m.Title,StringComparer.Ordinal).ToList();
 private List<StashMarkerValue> LocalMarkers(Guid mediaId,SqliteTransaction transaction)
 {
  List<T> Read<T>(string table)
  {
   var result=new List<T>();using var command=Command($"SELECT document FROM {table} WHERE media_id=$id",transaction,("$id",mediaId.ToString()));
   using var reader=command.ExecuteReader();while(reader.Read())result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0),Json)!);return result;
  }
  var scenes=Read<Scene>("scenes").Select(s=>new StashMarkerValue(s.Title,s.StartSeconds,s.EndSeconds,s.Tags));
  var bookmarks=Read<Bookmark>("bookmarks").Where(b=>b.Kind==BookmarkKind.Bookmark).Select(b=>new StashMarkerValue(b.Title,b.PositionSeconds,null,b.Tags));
  return SortedMarkers(scenes.Concat(bookmarks));
 }
 private static List<StashMarkerValue> SourceMarkers(IEnumerable<StashMarkerDraft> markers)=>SortedMarkers(markers.Select(m=>new StashMarkerValue(m.Title,m.StartSeconds,m.EndSeconds,NormalizeSceneTags(m.Tags))));

 /// <summary>Adds source marks missing locally; local marks are never removed or changed.</summary>
 private void AddStashMarkers(Guid mediaId,IEnumerable<StashMarkerDraft> markers,SqliteTransaction transaction)
 {
  var local=LocalMarkers(mediaId,transaction);
  foreach(var marker in SourceMarkers(markers))
  {
   if(local.Any(m=>m.Start==marker.Start&&m.End==marker.End&&m.Title==marker.Title))continue;
   if(!double.IsFinite(marker.Start)||marker.Start<0||marker.End is {} end&&(!double.IsFinite(end)||end<=marker.Start))
    throw new ArgumentException(T("A Stash marker has an invalid time range: {0}",marker.Title));
   if(marker.End is {} stop)SaveScene(new Scene{MediaId=mediaId,Title=marker.Title,StartSeconds=marker.Start,EndSeconds=stop,Tags=marker.Tags},transaction);
   else SaveBookmark(ValidateBookmark(new Bookmark{MediaId=mediaId,Title=marker.Title,PositionSeconds=marker.Start,Tags=marker.Tags}),transaction);
  }
 }

 /// <summary>Source and local values of the fields this import manages. A field absent from the source record is not observed, so it never deletes local data.</summary>
 private IReadOnlyList<StashFieldObservation> StashObservations(StashSourceDocument document,StashImportDraft draft,StashTargetReference target,(string Performer,string Studio) categories,
  IReadOnlyDictionary<long,StashSourceDocument> dependencies,StashSourceProfile source,SqliteTransaction transaction,IReadOnlyDictionary<long,TagKey>? selected=null)
 {
  var root=document.Document!.Value;var result=new List<StashFieldObservation>();
  JsonElement Value<T>(T value)=>JsonSerializer.SerializeToElement(value,Json);
  if(draft.Media is {} media)
  {
   var local=ReadMediaDocument(target.Id!.Value,transaction)??throw new InvalidOperationException("The local medium no longer exists.");
   if(media.Title!=null)result.Add(new("title",Value(media.Title),Value(local.Title)));
   if(media.Description!=null)result.Add(new("description",Value(media.Description),Value(local.Description)));
   if(media.Rating is {} rating)result.Add(new("rating",Value(Math.Clamp(rating,0,10)),Value(local.Rating)));
   if(media.ReleaseDate is {} date)result.Add(new("releaseDate",Value(date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)),Value(local.ReleaseDate?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture))));
   if(root.TryGetProperty("tags",out _)||root.TryGetProperty("performers",out _)||root.TryGetProperty("studio",out _))
   {
    var(plain,categorized)=StashSourceTags(draft,selected??new Dictionary<long,TagKey>(),dependencies,source,transaction,selected==null?categories:null);
    var categorizedLocal=local.TagCategories.SelectMany(pair=>pair.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
    result.Add(new("tags",StashTagValue(plain,categorized),StashTagValue(local.Tags.Where(t=>!categorizedLocal.Contains(t)),local.TagCategories)));
   }
   if(media.Kind==MediaKind.Video&&root.TryGetProperty("markers",out _))result.Add(new("markers",Value(SourceMarkers(media.Markers)),Value(LocalMarkers(local.Id,transaction))));
  }
  else if(draft.Directory is {} directory&&target.Tag is {} key)
  {
   var entry=ReadPreferences(transaction).TagVocabulary.FirstOrDefault(e=>Same(e.Tag,key.Value)&&Same(e.Category,key.Category));
   if(root.TryGetProperty("aliases",out _))result.Add(new("aliases",Value(directory.Aliases.Order(StringComparer.OrdinalIgnoreCase).ToArray()),Value((entry?.Aliases??[]).Order(StringComparer.OrdinalIgnoreCase).ToArray())));
   if(directory.Description!=null)result.Add(new("description",Value(directory.Description),Value(entry?.Description)));
   if(directory.Favorite is {} favorite)result.Add(new("favorite",Value(favorite),Value(entry?.IsFavorite??false)));
   if(directory.Profile!=null)result.Add(new("profile",Value(directory.Profile),Value(entry?.Profile)));
  }
  return result;
 }

 /// <summary>Applies the explicitly chosen source values and returns the field decisions to remember.</summary>
 private IReadOnlyList<StashFieldBaseline> StashDecide(StashImportRecord record,StashImportItem item,StashBinding? binding,StashTargetReference target,bool isNew,
  (string Performer,string Studio) categories,IReadOnlyDictionary<long,TagKey> selected,IReadOnlyDictionary<long,StashSourceDocument> dependencies,StashSourceProfile source,
  SqliteTransaction transaction,ref int created,ref int updated,ref int unchanged)
 {
  var observations=StashObservations(record.Document,record.Draft,target,categories,dependencies,source,transaction,selected);
  if(isNew)
  {
   // A new target already holds the source values; every observed field is remembered as taken from the source.
   if(item.Fields.Count>0)throw new ArgumentException("A new target takes every source field; field decisions apply to existing targets.");
   created++;return StashFieldConflicts.Record(observations,observations.Select(o=>new StashFieldChoice(o.Field,StashFieldDecision.UseSource)).ToArray());
  }
  var previous=binding!=null&&binding.Target==target?binding.Fields:[];
  var baselines=StashFieldConflicts.Record(observations,item.Fields,previous);
  var used=item.Fields.Where(choice=>choice.Decision==StashFieldDecision.UseSource).Select(choice=>choice.Field).ToHashSet(StringComparer.Ordinal);
  if(used.Count>0)
  {
   var values=observations.Where(o=>used.Contains(o.Field)).ToDictionary(o=>o.Field,o=>o.Source,StringComparer.Ordinal);
   if(record.Draft.Media is {} media)ApplyStashMediaFields(target.Id!.Value,media,values,record.Draft,selected,dependencies,source,transaction);
   else ApplyStashDirectoryFields(target.Tag!,record.Draft.Directory!,values,transaction);
   updated++;
  }
  else if(binding!=null&&binding.Target==target&&!binding.Detached&&baselines.Count==binding.Fields.Count)unchanged++;
  else updated++;
  return baselines;
 }

 private void ApplyStashMediaFields(Guid id,StashMediaDraft media,IReadOnlyDictionary<string,JsonElement> values,StashImportDraft draft,IReadOnlyDictionary<long,TagKey> selected,
  IReadOnlyDictionary<long,StashSourceDocument> dependencies,StashSourceProfile source,SqliteTransaction transaction)
 {
  var item=ReadMediaDocument(id,transaction)!;
  if(values.ContainsKey("title"))item=item with {Title=media.Title!};
  if(values.ContainsKey("description"))item=item with {Description=media.Description};
  if(values.ContainsKey("rating"))item=item with {Rating=Math.Clamp(media.Rating!.Value,0,10)};
  if(values.ContainsKey("releaseDate"))item=item with {ReleaseDate=media.ReleaseDate};
  if(values.ContainsKey("tags"))
  {
   var(plain,categorized)=StashSourceTags(draft,selected,dependencies,source,transaction);
   item=item with {Tags=plain.ToList(),TagCategories=categorized};
  }
  UpsertCore(item,transaction);
  if(values.ContainsKey("markers"))AddStashMarkers(id,media.Markers,transaction);
 }

 private void ApplyStashDirectoryFields(TagKey key,StashDirectoryDraft directory,IReadOnlyDictionary<string,JsonElement> values,SqliteTransaction transaction)
 {
  var preferences=ReadPreferences(transaction);var vocabulary=preferences.TagVocabulary.ToList();
  var index=vocabulary.FindIndex(e=>Same(e.Tag,key.Value)&&Same(e.Category,key.Category));
  var entry=index>=0?vocabulary[index]:new TagVocabularyEntry(key.Value,key.Category);
  if(values.ContainsKey("aliases"))entry=entry with {Aliases=directory.Aliases.Count>0?directory.Aliases.ToList():null};
  if(values.ContainsKey("description"))entry=entry with {Description=directory.Description};
  if(values.ContainsKey("favorite"))entry=entry with {IsFavorite=directory.Favorite!.Value};
  if(values.ContainsKey("profile"))entry=entry with {Profile=directory.Profile};
  if(index>=0)vocabulary[index]=entry;else vocabulary.Add(entry);
  var updated=preferences with {TagVocabulary=vocabulary};ValidatePreferences(updated);SaveDocument("preferences",Guid.Empty,updated,transaction);
 }
}
