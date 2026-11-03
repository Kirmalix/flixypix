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
 /// <summary>Adds the further mapped files of a scene or image as versions; without it they stay proposals. Versions are only added, never removed.</summary>
 public bool ConfirmVersions {get;init;}
}
/// <summary>An image inside an archive, resolved by the archive service: the cache file the catalog points to and the archive hash it was read from.</summary>
public sealed record StashArchiveMember(string ArchivePath,string EntryName,string CachePath,string ArchiveSha256,long Size);
/// <summary>Reads one archive member for an import, for example by extracting it into the archive cache.</summary>
public delegate Task<StashArchiveMember> StashArchiveResolver(string archivePath,string entryName,CancellationToken token);
public sealed record StashImportRequest(Guid SourceId,long ExpectedSourceRevision,string PerformerCategory,string StudioCategory,IReadOnlyList<StashImportItem> Items);
public sealed record StashImportResult(IReadOnlyList<StashBinding> Bindings,int Created,int Updated,int Unchanged,IReadOnlyList<string> Warnings);

public sealed partial class CatalogRepository
{
 private const int MaximumStashImportItems=256;
 internal static bool StashImportSupported(StashEntityKind kind)=>kind is StashEntityKind.Scene or StashEntityKind.Image or StashEntityKind.Performer or StashEntityKind.Studio
  or StashEntityKind.Tag or StashEntityKind.Gallery or StashEntityKind.Group;
 private sealed record StashImportRecord(StashSourceDocument Document,StashImportDraft Draft);
 private sealed record StashPlannedBinding(StashIdentity Identity,long? ExpectedIdentityRevision,StashSourceDocument Document,StashBinding? Current,StashTargetReference Target,
  IReadOnlyList<StashFieldBaseline> Fields,bool RetargetConfirmed);
 /// <summary>What one preview or apply knows about the selection: chosen categories, referenced records and the targets of records selected so far.</summary>
 private sealed class StashImportContext(StashSourceProfile source,(string Performer,string Studio) categories,IReadOnlyDictionary<long,StashSourceDocument> dependencies,bool preview)
 {
  public StashSourceProfile Source=>source;
  public (string Performer,string Studio) Categories=>categories;
  public IReadOnlyDictionary<long,StashSourceDocument> Dependencies=>dependencies;
  /// <summary>A preview shows prospective values for references that are neither bound nor selected; an apply refuses them.</summary>
  public bool Preview=>preview;
  public Dictionary<long,TagKey> Directory {get;}=[];
  public Dictionary<long,Guid> Media {get;}=[];
  /// <summary>Archive members resolved before the transaction, by source ordinal.</summary>
  public Dictionary<long,StashArchiveMember> Archives {get;}=[];
  public List<string> Warnings {get;}=[];
 }

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
   var context=new StashImportContext(source,categories,dependencies,true);var result=new List<StashImportPreviewRow>();
   foreach(var(document,draft)in records)
   {
    token.ThrowIfCancellationRequested();var identity=ResolveStashIdentity(source,document,transaction,token);
    var binding=identity is {State:StashIdentityState.Matched,Candidates:[var only]}?ReadEntity<StashBinding>("stash_bindings",only.Id,transaction):null;
    var lookup=new StashTargetLookup(this,transaction);if(binding!=null)binding=ObserveStashBinding(binding,draft.Kind,lookup);
    var matches=StashImportSupported(draft.Kind)?StashLocalMatches(draft,categories,transaction):[];
    var compared=draft.Kind==StashEntityKind.Tag?new StashTargetReference(StashTargetKind.ProvenanceOnly):binding is {Detached:false}?binding.Target:matches.Count==1?matches[0].Target:null;
    IReadOnlyList<StashFieldDiff> fields=[];
    if(compared!=null&&StashImportSupported(draft.Kind))
    {
     try{fields=StashFieldConflicts.Compare(StashObservations(new(document,draft),compared,context,transaction),binding?.Fields);}
     catch(Exception error)when(error is ArgumentException or InvalidOperationException){fields=[];}
    }
    result.Add(new(draft,identity,binding,matches,fields));
   }
   transaction.Commit();return result;
  }
 }

 /// <summary>Applies the selected records in one transaction: target writes, source identities, raw provenance and field decisions
 /// are published together or not at all. The source folder must still match the snapshot.</summary>
 /// <param name="archives">Resolves images that live inside archives; selecting such an image without it is refused.</param>
 public async Task<StashImportResult> ApplyStashImportAsync(StashExportSnapshot snapshot,StashImportRequest request,CancellationToken token=default,StashArchiveResolver? archives=null)
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
  // Archive members are read outside the catalog lock; the transaction only records the resolved cache files.
  var members=new Dictionary<long,StashArchiveMember>();
  foreach(var record in records)
  {
   if(record.Draft.Media is not {} media||StashMainPath(record.Draft)!=null||media.Files.FirstOrDefault(file=>file.ArchiveSource!=null) is not {} member)continue;
   if(media.Kind!=MediaKind.Image)throw new ArgumentException(T("Only images can be imported from inside an archive: {0}",record.Draft.RelativePath));
   if(member.ArchiveTarget==null||StashPaths.ArchiveEntry(member.OriginalPath,member.ArchiveSource!) is not {} entry)
    throw new ArgumentException(T("The image cannot be located inside its archive: {0}",member.OriginalPath));
   if(archives==null)throw new ArgumentException("Images inside archives need an archive resolver.");
   token.ThrowIfCancellationRequested();var resolved=await archives(member.ArchiveTarget,entry,token).ConfigureAwait(false);
   if(resolved==null||PathKey(resolved.ArchivePath)!=PathKey(member.ArchiveTarget)||resolved.EntryName!=entry||string.IsNullOrWhiteSpace(resolved.CachePath)||resolved.Size<0||
    resolved.ArchiveSha256.Length!=64||resolved.ArchiveSha256.Any(c=>!char.IsAsciiHexDigit(c)))throw new InvalidDataException("The archive resolver returned a different member.");
   members[record.Document.Ordinal]=resolved;
  }
  return await Task.Run(()=>ApplyStashImport(records,request,categories,dependencies,members,token),token).ConfigureAwait(false);
 }

 private StashImportResult ApplyStashImport(IReadOnlyList<StashImportRecord> records,StashImportRequest request,(string Performer,string Studio) categories,
  IReadOnlyDictionary<long,StashSourceDocument> dependencies,IReadOnlyDictionary<long,StashArchiveMember> members,CancellationToken token)
 {
  lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();
   var source=ReadEntity<StashSourceProfile>("stash_sources",request.SourceId,transaction)??throw new ArgumentException("Unknown Stash source.");
   if(source.Revision!=request.ExpectedSourceRevision)throw new InvalidOperationException("The Stash source changed after preview.");
   var context=new StashImportContext(source,categories,dependencies,false);var items=request.Items.ToDictionary(item=>item.SourceOrdinal);
   foreach(var(ordinal,member)in members)context.Archives[ordinal]=member;
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
   var planned=new List<StashPlannedBinding>();var counts=new int[3];
   void Plan(StashImportRecord record,StashTargetReference target,IReadOnlyList<StashFieldBaseline> fields,bool retarget)
   {var(identity,expected,binding)=identities[record.Document.Ordinal];planned.Add(new(identity,expected,record.Document,binding,target,fields,retarget));}
   // Dependency order: directory entries, media, tag parents, then image sets and groups, which refer to the media.
   foreach(var phase in new[]{new[]{StashEntityKind.Performer,StashEntityKind.Studio},[StashEntityKind.Scene,StashEntityKind.Image],[StashEntityKind.Tag],[StashEntityKind.Gallery],[StashEntityKind.Group]})
   foreach(var record in records.Where(r=>phase.Contains(r.Draft.Kind)))
   {
    token.ThrowIfCancellationRequested();var item=items[record.Document.Ordinal];var binding=identities[record.Document.Ordinal].Binding;
    if(record.Draft.Kind==StashEntityKind.Tag)
    {
     if(item.Target!=null||item.TargetName!=null||item.CreateTarget)throw new ArgumentException(T("Plain tags have no editable directory fields: {0}",record.Draft.Name));
     if(record.Draft.Directory is {} tag&&(tag.Aliases.Count>0||tag.Description!=null))context.Warnings.Add(T("Aliases and the description of a tag are kept only with its source record: {0}",record.Draft.Name));
     // Plain tags have no directory entry; the source record is kept as provenance and its parents go into the tag hierarchy.
     var provenanceOnly=new StashTargetReference(StashTargetKind.ProvenanceOnly);
     Plan(record,provenanceOnly,StashDecide(record,item,binding,provenanceOnly,binding==null,context,transaction,counts),binding!=null&&binding.Target!=provenanceOnly);
     continue;
    }
    if(record.Draft.Kind is StashEntityKind.Scene or StashEntityKind.Image)RequireStashDependencies(record.Draft,context,transaction);
    var(target,isNew,retarget)=StashTarget(record,item,binding,context,transaction);
    if(record.Draft.Kind is StashEntityKind.Performer or StashEntityKind.Studio)
    {
     if(context.Directory.Values.Any(other=>SameHierarchyKey(other,target.Tag!)))throw new ArgumentException(T("Two selected Stash entries would get the same directory name: {0}",target.Tag!.Value));
     context.Directory[record.Document.Ordinal]=target.Tag!;
     if(isNew)CreateStashDirectoryEntry(record,target.Tag!,transaction);
    }
    else if(isNew)target=new(target.Kind,record.Draft.Kind switch
    {
     StashEntityKind.Scene or StashEntityKind.Image=>CreateStashMedia(record,item,context,transaction).Id,
     StashEntityKind.Gallery=>CreateStashImageSet(record,context,transaction).Id,
     _=>CreateStashPublicationGroup(record,context,transaction).Id,
    });
    if(target.Kind is StashTargetKind.Video or StashTargetKind.Image)context.Media[record.Document.Ordinal]=target.Id!.Value;
    Plan(record,target,StashDecide(record,item,binding,target,isNew,context,transaction,counts),retarget);
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
   return new(bindings,counts[0],counts[1],counts[2],context.Warnings);
  }
 }

 private static (string Performer,string Studio) StashCategories(string performer,string studio)
 {
  static string Check(string? value)=>string.IsNullOrWhiteSpace(value)||value.Length>256||value.Any(char.IsControl)?throw new ArgumentException("Choose a category for performers and studios."):value.Trim();
  var result=(Check(performer),Check(studio));if(Same(result.Item1,result.Item2))throw new ArgumentException("Performers and studios need separate categories.");return result;
 }

 /// <summary>Referenced performer, studio and tag records and the members of galleries and groups, read before the catalog lock is taken.</summary>
 private static IReadOnlyDictionary<long,StashSourceDocument> ReadStashDependencies(StashExportSnapshot snapshot,IEnumerable<StashImportDraft> drafts,CancellationToken token)
 {
  var result=new Dictionary<long,StashSourceDocument>();
  void Add(long ordinal)
  {
   token.ThrowIfCancellationRequested();if(result.ContainsKey(ordinal))return;
   if(result.Count>=100_000)throw new ArgumentException("The selection refers to too many source records.");
   result[ordinal]=snapshot.ReadDocument(ordinal)??throw new InvalidDataException("A referenced Stash record is missing from the snapshot.");
  }
  foreach(var draft in drafts)
  {
   foreach(var reference in draft.References)
    if(reference.TargetOrdinal is {} ordinal&&reference.Reference.Kind is StashEntityKind.Performer or StashEntityKind.Studio or StashEntityKind.Tag)Add(ordinal);
   foreach(var member in draft.Members)Add(member.SourceOrdinal);
  }
  return result;
 }

 /// <summary>The active binding of a referenced record that was imported earlier.</summary>
 private StashBinding? StashBoundTarget(long ordinal,StashImportContext context,SqliteTransaction transaction)
 {
  if(!context.Dependencies.TryGetValue(ordinal,out var document))return null;
  var identity=ResolveStashIdentity(context.Source,document,transaction,CancellationToken.None);
  if(identity is not {State:StashIdentityState.Matched,Candidates:[var only]})return null;
  var binding=ReadEntity<StashBinding>("stash_bindings",only.Id,transaction);
  return binding is {Detached:false}&&new StashTargetLookup(this,transaction).Exists(binding.Target,document.Kind)?binding:null;
 }

 /// <summary>The local directory key of a referenced performer or studio: selected in this import, or bound earlier.</summary>
 private TagKey? StashDirectoryKey(long ordinal,StashImportContext context,SqliteTransaction transaction)
  =>context.Directory.TryGetValue(ordinal,out var key)?key:StashBoundTarget(ordinal,context,transaction) is {Target:{Kind:StashTargetKind.DirectoryEntry,Tag:{} bound}}?bound:null;

 private void RequireStashDependencies(StashImportDraft draft,StashImportContext context,SqliteTransaction transaction)
 {
  foreach(var reference in draft.References.Where(r=>r.Reference.Relation is "performer" or "studio"))
   if(StashDirectoryKey(reference.TargetOrdinal!.Value,context,transaction)==null)
    throw MissingStashDependency(reference.Reference.Relation,reference.Reference.Key["name:".Length..]);
 }

 private static ArgumentException MissingStashDependency(string relation,string name)=>new(relation=="performer"
  ?T("Select the referenced performer too, or import it first: {0}",name):T("Select the referenced studio too, or import it first: {0}",name));

 /// <summary>Local media of a gallery or group, in source order. Unresolved members refuse an apply; a preview returns null.</summary>
 private List<Guid>? StashMemberIds(StashImportDraft draft,StashImportContext context,SqliteTransaction transaction)
 {
  var expected=draft.Kind==StashEntityKind.Gallery?StashEntityKind.Image:StashEntityKind.Scene;var result=new List<Guid>();
  foreach(var member in draft.Members)
  {
   if(member.Kind!=expected)
   {
    if(!context.Preview)context.Warnings.Add(T("Only images can belong to an image set; other gallery entries are kept as provenance: {0}",member.SourceRelativePath));
    continue;
   }
   if(context.Media.TryGetValue(member.SourceOrdinal,out var id)){result.Add(id);continue;}
   if(StashBoundTarget(member.SourceOrdinal,context,transaction) is {Target:{Kind:StashTargetKind.Video or StashTargetKind.Image,Id:{} bound}}){result.Add(bound);continue;}
   if(context.Preview)return null;
   throw new ArgumentException(T("Select the referenced media too, or import them first: {0}",member.SourceRelativePath));
  }
  return result.Distinct().ToList();
 }

 /// <summary>The target of one record: explicit choice, the existing binding, or a new target.</summary>
 private (StashTargetReference Target,bool IsNew,bool Retarget) StashTarget(StashImportRecord record,StashImportItem item,StashBinding? binding,StashImportContext context,SqliteTransaction transaction)
 {
  var kind=record.Draft.Kind switch{StashEntityKind.Scene=>StashTargetKind.Video,StashEntityKind.Image=>StashTargetKind.Image,StashEntityKind.Gallery=>StashTargetKind.ImageSet,
   StashEntityKind.Group=>StashTargetKind.PublicationGroup,_=>StashTargetKind.DirectoryEntry};
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
   var key=NormalizeHierarchyKey(new TagKey(record.Draft.Kind==StashEntityKind.Performer?context.Categories.Performer:context.Categories.Studio,name));
   if(DirectoryKeyExists(key,transaction))throw new InvalidOperationException(T("A local directory entry already has this name. Choose it as the target or enter another name: {0}",name));
   return(new(kind,Tag:key),true,binding!=null);
  }
  if(item.TargetName!=null)throw new ArgumentException("A directory name is only used for a new entry.");
  if(kind is StashTargetKind.Video or StashTargetKind.Image)
  {
   var path=StashMainPath(record.Draft)??(context.Archives.TryGetValue(record.Document.Ordinal,out var member)?member.CachePath:null)
    ??throw new ArgumentException(T("The Stash record has no mapped local file: {0}",record.Draft.RelativePath));
   using var existing=Command("SELECT id FROM media WHERE path_key=$path",transaction,("$path",PathKey(path)));
   if(existing.ExecuteScalar()!=null)throw new InvalidOperationException(T("A local medium already uses this file. Choose it as the target: {0}",path));
  }
  return(new(kind,Guid.Empty),true,binding!=null);
 }

 private static string? StashMainPath(StashImportDraft draft)=>draft.Media?.Files.FirstOrDefault(file=>file.TargetPath!=null&&file.ArchiveSource==null)?.TargetPath;

 private bool DirectoryKeyExists(TagKey key,SqliteTransaction transaction)
 {
  var preferences=ReadPreferences(transaction);
  if(preferences.TagVocabulary.Any(entry=>Same(entry.Tag,key.Value)&&Same(entry.Category,key.Category)))return true;
  using(var command=Command("SELECT 1 FROM media_tags WHERE tag=$tag COLLATE NOCASE AND category=$category COLLATE NOCASE LIMIT 1",transaction,("$tag",key.Value),("$category",key.Category)))
   if(command.ExecuteScalar()!=null)return true;
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
  else if(draft.Kind==StashEntityKind.Gallery)
   result.AddRange(ReadDocuments<ImageSet>("image_sets",transaction).Where(set=>Same(set.Name,draft.Name)).Select(set=>new StashImportCandidate(new(StashTargetKind.ImageSet,set.Id),set.Name)));
  else if(draft.Kind==StashEntityKind.Group)
   result.AddRange(ReadDocuments<PublicationGroup>("publication_groups",transaction).Where(group=>Same(group.Name,draft.Name)).Select(group=>new StashImportCandidate(new(StashTargetKind.PublicationGroup,group.Id),group.Name)));
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

 private MediaItem CreateStashMedia(StashImportRecord record,StashImportItem selection,StashImportContext context,SqliteTransaction transaction)
 {
  var media=record.Draft.Media!;var(plain,categorized)=StashSourceTags(record.Draft,context,transaction);
  MediaItem item;
  if(context.Archives.TryGetValue(record.Document.Ordinal,out var member))
  {
   var file=media.Files.First(f=>f.ArchiveSource!=null);
   item=new MediaItem{Path=member.CachePath,Kind=MediaKind.Image,FileSize=member.Size,Width=file.Width is >0?file.Width:null,Height=file.Height is >0?file.Height:null,
    Extra=new(){["archivePath"]=member.ArchivePath,["entryName"]=member.EntryName,["sha256"]=member.ArchiveSha256}};
  }
  else
  {
   var file=media.Files.First(f=>f.TargetPath!=null&&f.ArchiveSource==null);
   item=new MediaItem{Path=file.TargetPath!,Kind=media.Kind,FileSize=file.Bytes,Width=file.Width is >0?file.Width:null,Height=file.Height is >0?file.Height:null,
    DurationSeconds=file.DurationSeconds,Technical=file.Technical};
  }
  item=item with
  {
   Title=media.Title??Path.GetFileNameWithoutExtension(item.Extra.GetValueOrDefault("entryName")??item.Path),Description=media.Description,Rating=Math.Clamp(media.Rating??0,0,10),
   ReleaseDate=media.ReleaseDate,Tags=plain.ToList(),TagCategories=categorized,ReviewState=media.InitialReviewState,
  };
  var saved=UpsertCore(item,transaction);
  AddStashVersions(saved.Id,record.Draft,selection,context,transaction);
  if(media.Kind==MediaKind.Video)AddStashMarkers(saved.Id,media.Markers,transaction);
  return saved;
 }

 /// <summary>Further mapped files outside archives become versions once confirmed; otherwise the import only mentions them. Returns whether a version was added.</summary>
 private bool AddStashVersions(Guid id,StashImportDraft draft,StashImportItem selection,StashImportContext context,SqliteTransaction transaction)
 {
  var item=ReadMediaDocument(id,transaction)!;var known=MediaVersions.All(item).Select(file=>PathKey(file.Path)).ToHashSet(StringComparer.Ordinal);
  var further=draft.Media!.Files.Where(file=>file.TargetPath!=null&&file.ArchiveSource==null&&!known.Contains(PathKey(file.TargetPath))).ToArray();
  if(further.Length==0)return false;
  if(!selection.ConfirmVersions){context.Warnings.Add(T("Further files stay proposals and are not added as versions yet: {0}",draft.Name));return false;}
  var files=item.Files.Concat(further.Select(file=>new MediaFile{Path=file.TargetPath!,FileSize=file.Bytes,Width=file.Width is >0?file.Width:null,Height=file.Height is >0?file.Height:null,
   DurationSeconds=file.DurationSeconds,Technical=file.Technical})).ToList();
  UpsertCore(item with {Files=files},transaction);return true;
 }

 private static string? StashText(StashSourceDocument document,string property)=>document.Document is {} root&&root.TryGetProperty(property,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;

 private ImageSet CreateStashImageSet(StashImportRecord record,StashImportContext context,SqliteTransaction transaction)
 {
  var members=StashMemberIds(record.Draft,context,transaction)!;
  var set=new ImageSet{Name=record.Draft.Name.Trim(),Description=StashText(record.Document,"details"),MediaIds=members,CoverId=members.Count>0?members[0]:null};
  ValidateImageSet(set);ValidateImageSetMembers(set,transaction);SaveImageSet(set,transaction);return set;
 }

 private PublicationGroup CreateStashPublicationGroup(StashImportRecord record,StashImportContext context,SqliteTransaction transaction)
 {
  var members=StashMemberIds(record.Draft,context,transaction)!;
  var group=new PublicationGroup{Name=record.Draft.Name.Trim(),Description=StashText(record.Document,"synopsis"),Members=members.Select(id=>new PublicationGroupMember(PublicationGroupMemberKind.Video,id)).ToList()};
  ValidatePublicationGroup(group);ValidatePublicationGroupTargets(group,transaction);SavePublicationGroup(group,transaction);return group;
 }

 private (IReadOnlyList<string> Plain,Dictionary<string,List<string>> Categorized) StashSourceTags(StashImportDraft draft,StashImportContext context,SqliteTransaction transaction)
 {
  var plain=draft.Media!.Tags.Where(t=>!string.IsNullOrWhiteSpace(t)).Select(t=>t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
  var categorized=new Dictionary<string,List<string>>(StringComparer.OrdinalIgnoreCase);
  foreach(var reference in draft.References.Where(r=>r.Reference.Relation is "performer" or "studio"))
  {
   var name=reference.Reference.Key["name:".Length..];
   var key=StashDirectoryKey(reference.TargetOrdinal!.Value,context,transaction)
    ??(context.Preview?new TagKey(reference.Reference.Relation=="performer"?context.Categories.Performer:context.Categories.Studio,name.Trim())
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

 private static TagKey StashTagKey(StashImportDraft draft)=>NormalizeHierarchyKey(new TagKey("",draft.Name));
 private static IEnumerable<TagKey> StashSourceParents(StashImportDraft draft)=>draft.References.Where(r=>r.Reference.Relation=="parent")
  .Select(r=>NormalizeHierarchyKey(new TagKey("",r.Reference.Key["name:".Length..]))).Distinct(TagAssetTagComparer.Instance);
 private static JsonElement StashParentValue(IEnumerable<TagKey> parents)=>JsonSerializer.SerializeToElement(parents.Select(p=>new[]{p.Category,p.Value})
  .OrderBy(p=>p[0],StringComparer.OrdinalIgnoreCase).ThenBy(p=>p[1],StringComparer.OrdinalIgnoreCase).ToArray(),Json);

 /// <summary>Source and local values of the fields this import manages. A field absent from the source record is not observed, so it never deletes local data.</summary>
 private IReadOnlyList<StashFieldObservation> StashObservations(StashImportRecord record,StashTargetReference target,StashImportContext context,SqliteTransaction transaction)
 {
  var(document,draft)=(record.Document,record.Draft);var root=document.Document!.Value;var result=new List<StashFieldObservation>();
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
    var(plain,categorized)=StashSourceTags(draft,context,transaction);
    var categorizedLocal=local.TagCategories.SelectMany(pair=>pair.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
    result.Add(new("tags",StashTagValue(plain,categorized),StashTagValue(local.Tags.Where(t=>!categorizedLocal.Contains(t)),local.TagCategories)));
   }
   if(media.Kind==MediaKind.Video&&root.TryGetProperty("markers",out _))result.Add(new("markers",Value(SourceMarkers(media.Markers)),Value(LocalMarkers(local.Id,transaction))));
  }
  else if(draft.Kind==StashEntityKind.Tag)
  {
   if(root.TryGetProperty("parents",out _))
   {
    var key=StashTagKey(draft);
    result.Add(new("parents",StashParentValue(StashSourceParents(draft)),StashParentValue(ReadTagHierarchy(transaction).Edges.Where(e=>SameHierarchyKey(e.Child,key)).Select(e=>e.Parent))));
   }
  }
  else if(target.Kind==StashTargetKind.ImageSet)
  {
   var set=ReadEntity<ImageSet>("image_sets",target.Id!.Value,transaction)??throw new InvalidOperationException("The local image set no longer exists.");
   result.Add(new("name",Value(draft.Name.Trim()),Value(set.Name)));
   if(StashText(document,"details") is {} details)result.Add(new("description",Value(details),Value(set.Description)));
   if(StashMemberIds(draft,context,transaction) is {} members)result.Add(new("mediaIds",Value(members),Value(set.MediaIds)));
  }
  else if(target.Kind==StashTargetKind.PublicationGroup)
  {
   var group=ReadEntity<PublicationGroup>("publication_groups",target.Id!.Value,transaction)??throw new InvalidOperationException("The local publication group no longer exists.");
   result.Add(new("name",Value(draft.Name.Trim()),Value(group.Name)));
   if(StashText(document,"synopsis") is {} synopsis)result.Add(new("description",Value(synopsis),Value(group.Description)));
   if(StashMemberIds(draft,context,transaction) is {} members)
    result.Add(new("members",Value(members.Select(id=>new PublicationGroupMember(PublicationGroupMemberKind.Video,id)).ToList()),Value(group.Members)));
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

 /// <summary>Applies the explicitly chosen source values and returns the field decisions to remember. counts: created, updated, unchanged.</summary>
 private IReadOnlyList<StashFieldBaseline> StashDecide(StashImportRecord record,StashImportItem item,StashBinding? binding,StashTargetReference target,bool isNew,
  StashImportContext context,SqliteTransaction transaction,int[] counts)
 {
  if(isNew&&item.Fields.Count>0)throw new ArgumentException("A new target takes every source field; field decisions apply to existing targets.");
  if(isNew&&record.Draft.Kind==StashEntityKind.Tag)
  {
   // A first import adds the source parents; existing local parents of the plain tag stay.
   var key=StashTagKey(record.Draft);ChangeStashTagParents(key,current=>current.Concat(StashSourceParents(record.Draft)),transaction);
  }
  var observations=StashObservations(record,target,context,transaction);
  if(isNew)
  {
   // A new target already holds the source values; every observed field is remembered as taken from the source.
   counts[0]++;return StashFieldConflicts.Record(observations,observations.Select(o=>new StashFieldChoice(o.Field,StashFieldDecision.UseSource)).ToArray());
  }
  var previous=binding!=null&&binding.Target==target?binding.Fields:[];
  var baselines=StashFieldConflicts.Record(observations,item.Fields,previous);
  var used=item.Fields.Where(choice=>choice.Decision==StashFieldDecision.UseSource).Select(choice=>choice.Field).ToHashSet(StringComparer.Ordinal);
  var versioned=record.Draft.Media!=null&&AddStashVersions(target.Id!.Value,record.Draft,item,context,transaction);
  if(versioned&&used.Count==0)counts[1]++;
  else if(used.Count>0)
  {
   if(record.Draft.Media is {} media)ApplyStashMediaFields(target.Id!.Value,media,used,record.Draft,context,transaction);
   else if(record.Draft.Kind==StashEntityKind.Tag)ChangeStashTagParents(StashTagKey(record.Draft),_=>StashSourceParents(record.Draft),transaction);
   else if(target.Kind==StashTargetKind.ImageSet)ApplyStashImageSetFields(target.Id!.Value,record,used,context,transaction);
   else if(target.Kind==StashTargetKind.PublicationGroup)ApplyStashGroupFields(target.Id!.Value,record,used,context,transaction);
   else ApplyStashDirectoryFields(target.Tag!,record.Draft.Directory!,used,transaction);
   counts[1]++;
  }
  else if(binding!=null&&binding.Target==target&&!binding.Detached&&baselines.Count==binding.Fields.Count)counts[2]++;
  else counts[1]++;
  return baselines;
 }

 private void ApplyStashMediaFields(Guid id,StashMediaDraft media,IReadOnlySet<string> fields,StashImportDraft draft,StashImportContext context,SqliteTransaction transaction)
 {
  var item=ReadMediaDocument(id,transaction)!;
  if(fields.Contains("title"))item=item with {Title=media.Title!};
  if(fields.Contains("description"))item=item with {Description=media.Description};
  if(fields.Contains("rating"))item=item with {Rating=Math.Clamp(media.Rating!.Value,0,10)};
  if(fields.Contains("releaseDate"))item=item with {ReleaseDate=media.ReleaseDate};
  if(fields.Contains("tags"))
  {
   var(plain,categorized)=StashSourceTags(draft,context,transaction);
   item=item with {Tags=plain.ToList(),TagCategories=categorized};
  }
  UpsertCore(item,transaction);
  if(fields.Contains("markers"))AddStashMarkers(id,media.Markers,transaction);
 }

 private void ApplyStashImageSetFields(Guid id,StashImportRecord record,IReadOnlySet<string> fields,StashImportContext context,SqliteTransaction transaction)
 {
  var set=ReadEntity<ImageSet>("image_sets",id,transaction)!;
  if(fields.Contains("name"))set=set with {Name=record.Draft.Name.Trim()};
  if(fields.Contains("description"))set=set with {Description=StashText(record.Document,"details")};
  if(fields.Contains("mediaIds"))
  {
   var members=StashMemberIds(record.Draft,context,transaction)!;
   set=set with {MediaIds=members,CoverId=set.CoverId is {} cover&&members.Contains(cover)?cover:members.Count>0?members[0]:null};
  }
  ValidateImageSet(set);ValidateImageSetMembers(set,transaction);SaveImageSet(set,transaction);
 }

 private void ApplyStashGroupFields(Guid id,StashImportRecord record,IReadOnlySet<string> fields,StashImportContext context,SqliteTransaction transaction)
 {
  var group=ReadEntity<PublicationGroup>("publication_groups",id,transaction)!;
  if(fields.Contains("name"))group=group with {Name=record.Draft.Name.Trim()};
  if(fields.Contains("description"))group=group with {Description=StashText(record.Document,"synopsis")};
  if(fields.Contains("members"))group=group with {Members=StashMemberIds(record.Draft,context,transaction)!.Select(m=>new PublicationGroupMember(PublicationGroupMemberKind.Video,m)).ToList()};
  ValidatePublicationGroup(group);ValidatePublicationGroupTargets(group,transaction);SavePublicationGroup(group,transaction);
 }

 /// <summary>Sets the parents of a plain tag in the hierarchy; a cycle refuses the whole import.</summary>
 private void ChangeStashTagParents(TagKey child,Func<IEnumerable<TagKey>,IEnumerable<TagKey>> change,SqliteTransaction transaction)
 {
  var graph=ReadTagHierarchy(transaction);
  var parents=change(graph.Edges.Where(e=>SameHierarchyKey(e.Child,child)).Select(e=>e.Parent)).Distinct(TagAssetTagComparer.Instance).ToArray();
  var updated=graph with {Edges=NormalizeHierarchyEdges(graph.Edges.Where(e=>!SameHierarchyKey(e.Child,child)).Concat(parents.Select(p=>new TagParentEdge(p,child))))};
  ValidateHierarchy(updated);SaveTagHierarchyIfChanged(graph,updated,transaction);
 }

 private void ApplyStashDirectoryFields(TagKey key,StashDirectoryDraft directory,IReadOnlySet<string> fields,SqliteTransaction transaction)
 {
  var preferences=ReadPreferences(transaction);var vocabulary=preferences.TagVocabulary.ToList();
  var index=vocabulary.FindIndex(e=>Same(e.Tag,key.Value)&&Same(e.Category,key.Category));
  var entry=index>=0?vocabulary[index]:new TagVocabularyEntry(key.Value,key.Category);
  if(fields.Contains("aliases"))entry=entry with {Aliases=directory.Aliases.Count>0?directory.Aliases.ToList():null};
  if(fields.Contains("description"))entry=entry with {Description=directory.Description};
  if(fields.Contains("favorite"))entry=entry with {IsFavorite=directory.Favorite!.Value};
  if(fields.Contains("profile"))entry=entry with {Profile=directory.Profile};
  if(index>=0)vocabulary[index]=entry;else vocabulary.Add(entry);
  var updated=preferences with {TagVocabulary=vocabulary};ValidatePreferences(updated);SaveDocument("preferences",Guid.Empty,updated,transaction);
 }
}
