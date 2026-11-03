using System.Globalization;
using System.Text.Json;

namespace FlixyPix.Core;

public sealed record StashResolvedReference(StashSourceReference Reference,long? TargetOrdinal);
public sealed record StashFileDraft(string OriginalPath,string? TargetPath,bool? Exists,long? FileRecordOrdinal,
 long? Bytes,int? Width,int? Height,double? DurationSeconds,TechnicalInfo? Technical,string? ArchiveSource=null,string? ArchiveTarget=null);
public sealed record StashMarkerDraft(int Position,string Title,double StartSeconds,double? EndSeconds,IReadOnlyList<string> Tags)
{public bool IsScene=>EndSeconds.HasValue;}
public sealed record StashMediaDraft(MediaKind Kind,string? Title,string? Description,int? Rating,DateOnly? ReleaseDate,bool? SourceOrganized,
 IReadOnlyList<string> Tags,IReadOnlyList<StashFileDraft> Files,IReadOnlyList<StashMarkerDraft> Markers)
{
 // Multiple files remain proposals until explicitly confirmed as versions.
 public ReviewState InitialReviewState=>ReviewState.Unreviewed;
}
public sealed record StashDirectoryDraft(string Name,TagRole Role,IReadOnlyList<string> Aliases,bool? Favorite,string? Description,TagProfile? Profile,string? Disambiguation);
public sealed record StashMemberDraft(long SourceOrdinal,StashEntityKind Kind,int? SourceOrder,string SourceRelativePath);
/// <summary>Source proposals have no target IDs. Persistent identity/conflict resolution and explicit confirmation precede any catalog write.</summary>
public sealed record StashImportDraft(long SourceOrdinal,StashEntityKind Kind,string RelativePath,string SourceSha256,string Name,
 StashMediaDraft? Media,StashDirectoryDraft? Directory,IReadOnlyList<StashResolvedReference> References,IReadOnlyList<StashMemberDraft> Members,IReadOnlyList<StashReadIssue> Issues)
{public bool HasErrors=>Issues.Any(issue=>issue.Severity==StashIssueSeverity.Error);}

public sealed partial class StashExportSnapshot
{
 /// <summary>Projects a bounded source page. Only explicitly mapped local paths are passed to the availability check.</summary>
 public IReadOnlyList<StashImportDraft> ReadDraftPage(StashSourceProfile profile,int offset,int limit=24,Func<string,bool>? exists=null,CancellationToken token=default)
 {
  CatalogRepository.ValidateStashSource(profile);
  if(CatalogRepository.PathKey(profile.SourceDirectory)!=CatalogRepository.PathKey(SourceDirectory))throw new ArgumentException("The source profile belongs to a different export folder.");
  exists??=File.Exists;var selected=profile with {PathMappings=profile.PathMappings.ToList()};
  lock(gate)
  {
   ObjectDisposedException.ThrowIf(disposed,this);var result=new List<StashImportDraft>();
   foreach(var record in ReadPage(offset,limit))
   {
    token.ThrowIfCancellationRequested();result.Add(Project(record,selected,exists,token));
   }
   return result;
  }
 }
 /// <summary>The same projection for one record, so the apply step can read exactly the selected rows.</summary>
 internal (StashSourceDocument Document,StashImportDraft Draft)? ReadDraft(StashSourceProfile profile,long ordinal,Func<string,bool> exists,CancellationToken token)
 {
  CatalogRepository.ValidateStashSource(profile);
  if(CatalogRepository.PathKey(profile.SourceDirectory)!=CatalogRepository.PathKey(SourceDirectory))throw new ArgumentException("The source profile belongs to a different export folder.");
  var selected=profile with {PathMappings=profile.PathMappings.ToList()};
  lock(gate)
  {
   ObjectDisposedException.ThrowIf(disposed,this);token.ThrowIfCancellationRequested();
   return ReadDocument(ordinal) is {} record?(record,Project(record,selected,exists,token)):null;
  }
 }
 private StashImportDraft Project(StashSourceDocument record,StashSourceProfile profile,Func<string,bool> exists,CancellationToken token)
 {
  var issues=record.Issues.ToList();
  void Issue(string code,string message,bool error=true)
  {if(issues.Count>=256){if(!error||issues.Any(issue=>issue.Severity==StashIssueSeverity.Error))return;issues.RemoveAt(255);}issues.Add(new(code,error?StashIssueSeverity.Error:StashIssueSeverity.Warning,message));}
  string name=Path.GetFileNameWithoutExtension(record.RelativePath);var references=new List<StashResolvedReference>();var members=new List<StashMemberDraft>();StashMediaDraft? media=null;StashDirectoryDraft? directory=null;
  if(record.Valid&&record.Document is {} root)
  {
   name=Text(root,record.Kind is StashEntityKind.Performer or StashEntityKind.Studio or StashEntityKind.Tag or StashEntityKind.Group?"name":"title")??name;
   using(var command=connection.CreateCommand())
   {
    command.CommandText="SELECT kind,key,relation,position,source_order FROM source_links WHERE owner=$owner ORDER BY position,relation";command.Parameters.AddWithValue("$owner",record.Ordinal);using var reader=command.ExecuteReader();
    while(reader.Read())
    {
     token.ThrowIfCancellationRequested();if(references.Count>=100000){Issue("reference-count","This source record exceeds the supported reference count.");break;}
     var reference=new StashSourceReference((StashEntityKind)reader.GetInt32(0),reader.GetString(1),reader.GetString(2),reader.GetInt32(3),reader.IsDBNull(4)?null:reader.GetInt32(4));
     var matches=ReferenceTargets(reference.Kind,reference.Key);
     if(matches.Count!=1)Issue(matches.Count==0?"missing-reference":"ambiguous-reference",reference.Relation+": "+reference.Key,reference.Relation!="file");
     references.Add(new(reference,matches.Count==1?matches[0]:null));
    }
   }
   if(record.Kind is StashEntityKind.Scene or StashEntityKind.Image)
   {
    var files=new List<StashFileDraft>();var markers=new List<StashMarkerDraft>();
    var fileReferences=references.Where(reference=>reference.Reference.Relation=="file").GroupBy(reference=>reference.Reference.Key,StringComparer.Ordinal).ToDictionary(group=>group.Key,group=>group.First(),StringComparer.Ordinal);
    string? Map(string path)
    {try{return StashPaths.Map(path,profile.PathMappings).TargetPath;}catch(ArgumentException){Issue("mapped-path","The source path cannot be represented beneath the selected destination: "+path);return null;}}
    foreach(var path in Strings(root,"files"))
    {
     token.ThrowIfCancellationRequested();if(files.Count>=100000){Issue("file-count","Too many proposed file versions.");break;}
     var target=Map(path);if(target==null)Issue("unmapped-path","No usable selected prefix mapping for "+path);
     fileReferences.TryGetValue(StashExport.ReferencePath(path),out var fileReference);
     var source=fileReference?.TargetOrdinal is {} ordinal?ReadOriginal(ordinal):null;
     if(source is {} metadata&&Text(metadata,"type")!=(record.Kind==StashEntityKind.Scene?"video":"image"))Issue("file-kind","The referenced file type does not match its medium.");
     var archive=source is {} data?Text(data,"zip_file"):null;string? archiveTarget=null;bool? available=null;
     if(!string.IsNullOrEmpty(archive)){archiveTarget=Map(archive);if(archiveTarget==null)Issue("unmapped-archive","No usable selected prefix mapping for archive "+archive);Issue("archive-member","Archive members need explicit archive resolution; their member paths are not treated as extracted files.",false);}
     else if(target!=null){available=exists(target);if(!available.Value)Issue("missing-file","The mapped media file is unavailable: "+target,false);}
     files.Add(new(path,target,available,fileReference?.TargetOrdinal,source is {} s?Long(s,"size"):null,source is {} w?Int(w,"width"):null,source is {} h?Int(h,"height"):null,
      source is {} duration?Number(duration,"duration"):null,source is {} technical?new TechnicalInfo{VideoCodec=Text(technical,"video_codec"),AudioCodec=Text(technical,"audio_codec"),Container=Text(technical,"format"),FrameRate=Number(technical,"frame_rate"),BitRate=Long(technical,"bitrate")}:null,archive,archiveTarget));
    }
    if(root.TryGetProperty("markers",out var markerArray)&&markerArray.ValueKind==JsonValueKind.Array)
    {
     foreach(var marker in markerArray.EnumerateArray())
     {
      token.ThrowIfCancellationRequested();if(markers.Count>=100000){Issue("marker-count","Too many source markers.");break;}
      var start=double.Parse(marker.GetProperty("seconds").GetString()!,CultureInfo.InvariantCulture);var endText=Text(marker,"end_seconds");var end=endText==null?(double?)null:double.Parse(endText,CultureInfo.InvariantCulture);
      markers.Add(new(markers.Count,Text(marker,"title")??"",start,end,Strings(marker,"tags").Concat(Text(marker,"primary_tag")is {Length:>0} tag?[tag]:[]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
     }
    }
    media=new(record.Kind==StashEntityKind.Scene?MediaKind.Video:MediaKind.Image,Text(root,"title"),Text(root,"details"),Int(root,"rating")is {} rating?(int)Math.Round(rating/10d,MidpointRounding.AwayFromZero):null,Date(root,"date",Issue),Bool(root,"organized"),Strings(root,"tags"),files,markers);
   }
   else if(record.Kind is StashEntityKind.Performer or StashEntityKind.Studio or StashEntityKind.Tag)
   {
    var aliases=Strings(root,"aliases");if(record.Kind==StashEntityKind.Performer&&Text(root,"aliases")is {} text)aliases=text.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
    TagProfile? details=null;
    if(record.Kind==StashEntityKind.Performer)
    {
     int? IntegerText(string property)
     {
      var value=Text(root,property);if(string.IsNullOrWhiteSpace(value))return null;if(int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var parsed)&&parsed>0)return parsed;Issue("profile-number","Invalid source profile field: "+property,false);return null;
     }
     PerformerGender? gender=Text(root,"gender")?.ToUpperInvariant() switch{"FEMALE"=>PerformerGender.Female,"MALE"=>PerformerGender.Male,"TRANSGENDER_FEMALE"=>PerformerGender.TransFemale,"TRANSGENDER_MALE"=>PerformerGender.TransMale,"NON_BINARY"=>PerformerGender.NonBinary,"INTERSEX"=>PerformerGender.Intersex,_=>null};
     if(gender==null&&!string.IsNullOrWhiteSpace(Text(root,"gender")))Issue("profile-gender","Unrecognized source gender is retained in the original document.",false);
     details=new(){Gender=gender,BirthDate=Date(root,"birthdate",Issue),DeathDate=Date(root,"death_date",Issue),Country=Text(root,"country"),Ethnicity=Text(root,"ethnicity"),EyeColor=Text(root,"eye_color"),HairColor=Text(root,"hair_color"),HeightCm=IntegerText("height"),WeightKg=Int(root,"weight"),Measurements=Text(root,"measurements"),Tattoos=Text(root,"tattoos"),Piercings=Text(root,"piercings"),CareerStart=IntegerText("career_start"),CareerEnd=IntegerText("career_end"),Links=Strings(root,"urls").ToList()};
    }
    else if(record.Kind==StashEntityKind.Studio)details=new(){ParentStudio=Text(root,"parent_studio"),Links=Strings(root,"urls").ToList()};
    if(details!=null)try{details.Validate(DateOnly.FromDateTime(DateTime.Today));}catch(ArgumentException exception){Issue("profile-value",exception.Message);}
    directory=new(name,record.Kind==StashEntityKind.Performer?TagRole.Performer:record.Kind==StashEntityKind.Studio?TagRole.Studio:TagRole.Tag,aliases,Bool(root,"favorite"),Text(root,"details")??Text(root,"description"),details,Text(root,"disambiguation"));
   }
   if(record.Kind is StashEntityKind.Group or StashEntityKind.Gallery)
   {
    var relation=record.Kind==StashEntityKind.Group?"group":"gallery";
    using var command=connection.CreateCommand();command.CommandText="""
     SELECT d.ordinal,d.kind,d.path,MIN(l.source_order),COALESCE(json_extract(d.original,'$.files[0]'),d.path) AS file_order
     FROM source_links l JOIN reference_keys r ON r.kind=l.kind AND r.key=l.key JOIN documents d ON d.ordinal=l.owner
     WHERE r.ordinal=$target AND l.relation=$relation AND (SELECT count(*) FROM reference_keys u WHERE u.kind=l.kind AND u.key=l.key)=1
     GROUP BY d.ordinal ORDER BY COALESCE(MIN(l.source_order),2147483647),file_order,d.path LIMIT 100001
     """;command.Parameters.AddWithValue("$target",record.Ordinal);command.Parameters.AddWithValue("$relation",relation);using var reader=command.ExecuteReader();
    while(reader.Read()){token.ThrowIfCancellationRequested();if(members.Count==100000){Issue("member-count","This group or gallery exceeds the supported member count.");break;}members.Add(new(reader.GetInt64(0),(StashEntityKind)reader.GetInt32(1),reader.IsDBNull(3)?null:reader.GetInt32(3),reader.GetString(2)));}
    if(record.Kind==StashEntityKind.Gallery&&members.Count>0)Issue("derived-image-order","Image order is derived from source file paths; the folder export does not carry explicit gallery image positions.",false);
   }
  }
  return new(record.Ordinal,record.Kind,record.RelativePath,record.Sha256,name,media,directory,references,members,issues);
 }
 private IReadOnlyList<long> ReferenceTargets(StashEntityKind kind,string key)
 {
  using var command=connection.CreateCommand();command.CommandText="SELECT ordinal FROM reference_keys WHERE kind=$kind AND key=$key ORDER BY ordinal LIMIT 2";command.Parameters.AddWithValue("$kind",(int)kind);command.Parameters.AddWithValue("$key",key);using var reader=command.ExecuteReader();var result=new List<long>();while(reader.Read())result.Add(reader.GetInt64(0));return result;
 }
 private JsonElement? ReadOriginal(long ordinal)
 {using var command=connection.CreateCommand();command.CommandText="SELECT original FROM documents WHERE ordinal=$id";command.Parameters.AddWithValue("$id",ordinal);if(command.ExecuteScalar()is not byte[] bytes)return null;using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=64});return document.RootElement.Clone();}
 private static string? Text(JsonElement value,string property)=>value.TryGetProperty(property,out var field)&&field.ValueKind==JsonValueKind.String?field.GetString():null;
 private static string[] Strings(JsonElement value,string property)=>value.TryGetProperty(property,out var field)&&field.ValueKind==JsonValueKind.Array?field.EnumerateArray().Where(item=>item.ValueKind==JsonValueKind.String).Select(item=>item.GetString()!).ToArray():[];
 private static int? Int(JsonElement value,string property)=>value.TryGetProperty(property,out var field)&&field.ValueKind==JsonValueKind.Number&&field.TryGetInt32(out var number)?number:null;
 private static long? Long(JsonElement value,string property)=>value.TryGetProperty(property,out var field)&&field.ValueKind==JsonValueKind.Number&&field.TryGetInt64(out var number)?number:null;
 private static double? Number(JsonElement value,string property)=>value.TryGetProperty(property,out var field)&&field.ValueKind==JsonValueKind.Number&&field.TryGetDouble(out var number)?number:null;
 private static bool? Bool(JsonElement value,string property)=>value.TryGetProperty(property,out var field)&&field.ValueKind is JsonValueKind.True or JsonValueKind.False?field.GetBoolean():null;
 private static DateOnly? Date(JsonElement value,string property,Action<string,string,bool> issue)
 {
  var text=Text(value,property);if(string.IsNullOrWhiteSpace(text))return null;if(DateOnly.TryParseExact(text,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))return date;issue("date","Invalid source date: "+property,false);return null;
 }
}
