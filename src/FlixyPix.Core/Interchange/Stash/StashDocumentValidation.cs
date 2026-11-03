using System.Globalization;
using System.Text.Json;

namespace FlixyPix.Core;

public static partial class StashExport
{
 internal static (StashEntityKind Kind,string? NaturalKey,List<StashReadIssue> Issues) ValidateDocument(StashEntityKind kind,JsonElement root)
 {
  var issues=new List<StashReadIssue>();
  void Error(string code,string message)
  {if(issues.Count<256)issues.Add(new(code,StashIssueSeverity.Error,message));else if(!issues.Any(issue=>issue.Severity==StashIssueSeverity.Error))issues[^1]=new(code,StashIssueSeverity.Error,message);}
  void Warning(string code,string message){if(issues.Count<256)issues.Add(new(code,StashIssueSeverity.Warning,message));}
  if(root.ValueKind!=JsonValueKind.Object){Error("object","Expected a Stash JSON object.");return(kind,null,issues);}
  Unique(root,Error);
  string? String(JsonElement value,string property,bool required=false)
  {
   if(!value.TryGetProperty(property,out var field)||field.ValueKind==JsonValueKind.Null){if(required)Error("required",property+" is required.");return null;}
   if(field.ValueKind!=JsonValueKind.String){Error("string",property+" must be a string.");return null;}
   var text=field.GetString();if(required&&string.IsNullOrWhiteSpace(text))Error("required",property+" cannot be empty.");return text;
  }
  JsonElement[] Array(JsonElement value,string property,JsonValueKind expected)
  {
   if(!value.TryGetProperty(property,out var field)||field.ValueKind==JsonValueKind.Null)return [];
   if(field.ValueKind!=JsonValueKind.Array){Error("array",property+" must be an array.");return [];}
   var items=field.EnumerateArray().ToArray();if(items.Any(item=>item.ValueKind!=expected))Error("array-item",property+" has an invalid element type.");return items.Where(item=>item.ValueKind==expected).ToArray();
  }
  double? MarkerTime(JsonElement marker,string property,bool required)
  {
   var text=String(marker,property,required);if(text==null)return null;
   if(!double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var seconds)||!double.IsFinite(seconds)||seconds<0){Error("marker-time",property+" must be a finite nonnegative seconds string.");return null;}return seconds;
  }
  void GalleryRef(JsonElement reference)
  {
   var folder=String(reference,"folder_path");var title=String(reference,"title");var zip=Array(reference,"zip_files",JsonValueKind.String);
   if(string.IsNullOrWhiteSpace(folder)&&zip.Length==0&&string.IsNullOrWhiteSpace(title))Error("gallery-reference","A gallery reference requires folder_path, zip_files or title.");
   if(!string.IsNullOrWhiteSpace(folder)&&zip.Length>0)Warning("gallery-reference-precedence","Gallery folder_path takes precedence over zip_files, matching Stash.");
  }
  foreach(var property in new[]{"title","details","date","studio","url"})_=String(root,property);
  foreach(var property in new[]{"tags","performers","urls"})_=Array(root,property,JsonValueKind.String);
  if(root.TryGetProperty("organized",out var organized)&&organized.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))Error("organized","organized must be a boolean; it is never a FlixyPix review state.");
  if(root.TryGetProperty("rating",out var rating)&&rating.ValueKind!=JsonValueKind.Null&&(!rating.TryGetInt32Safe(out var number)||number is <0 or >100))Error("rating","Stash rating must be an integer from0 to100.");
  string? key=null;
  switch(kind)
  {
   case StashEntityKind.Scene:
   case StashEntityKind.Image:
    var files=Array(root,"files",JsonValueKind.String);
    if(files.Length==0)Warning("no-files","The source has no file reference; no playable medium can be created without a selected mapping.");
    foreach(var value in files)if(string.IsNullOrWhiteSpace(value.GetString()))Error("file-reference","File references cannot be empty.");
    foreach(var gallery in Array(root,"galleries",JsonValueKind.Object))GalleryRef(gallery);
    if(kind==StashEntityKind.Scene)
    {
     foreach(var marker in Array(root,"markers",JsonValueKind.Object))
     {
      _=String(marker,"title");_=String(marker,"primary_tag");_=Array(marker,"tags",JsonValueKind.String);
      var start=MarkerTime(marker,"seconds",true);var end=MarkerTime(marker,"end_seconds",false);
      if(start.HasValue&&end.HasValue&&end<=start)Error("marker-range","A ranged marker must end after its start.");
     }
     foreach(var group in Array(root,"movies",JsonValueKind.Object))
     {
      _=String(group,"movieName",true);
      if(group.TryGetProperty("scene_index",out var index)&&!index.TryGetInt32Safe(out _))Error("group-index","scene_index must be an integer.");
     }
    }
    // Scene/image export filenames may change when titles change; they are provenance, not stable import IDs.
    break;
   case StashEntityKind.Gallery:
    var folder=String(root,"folder_path");var zips=Array(root,"zip_files",JsonValueKind.String);
    key=!string.IsNullOrWhiteSpace(folder)?"folder:"+folder:zips.Length>0?"zip:"+JsonSerializer.Serialize(zips.Select(value=>value.GetString()).ToArray()):String(root,"title");
    if(string.IsNullOrWhiteSpace(key))Warning("gallery-identity","The gallery lacks an unambiguous folder/archive/title identity.");
    if(root.TryGetProperty("chapters",out _)){_=Array(root,"chapters",JsonValueKind.Object);Warning("gallery-chapters","Gallery chapters are retained as provenance; image sets do not have chapters.");}break;
   case StashEntityKind.Performer:
    var name=String(root,"name",true);var disambiguation=String(root,"disambiguation");key=name==null?null:JsonSerializer.Serialize(new[]{name,disambiguation??""});
    if(root.TryGetProperty("aliases",out var aliases)&&aliases.ValueKind is not (JsonValueKind.String or JsonValueKind.Array or JsonValueKind.Null))Error("aliases","Performer aliases must be a string or string array.");
    else if(aliases.ValueKind==JsonValueKind.Array)_=Array(root,"aliases",JsonValueKind.String);
    foreach(var property in new[]{"height","birthdate","death_date","career_start","career_end"})_=String(root,property);break;
   case StashEntityKind.Tag:
    key=String(root,"name",true);_=Array(root,"parents",JsonValueKind.String);_=Array(root,"aliases",JsonValueKind.String);break;
   case StashEntityKind.Studio:
    key=String(root,"name",true);_=String(root,"parent_studio");break;
   case StashEntityKind.Group:
    key=String(root,"name",true);
    if(root.TryGetProperty("sub_groups",out _)){_=Array(root,"sub_groups",JsonValueKind.Object);Warning("subgroups","Subgroups are retained as provenance; publication groups remain flat.");}break;
   case StashEntityKind.File:
    key=String(root,"path",true);var type=String(root,"type",true);
    if(type=="folder")kind=StashEntityKind.Folder;
    else if(type is not ("video" or "image" or "file"))Error("file-type","Unknown Stash file type.");
    if(kind!=StashEntityKind.Folder&&(!root.TryGetProperty("size",out var size)||!size.TryGetInt64Safe(out var bytes)||bytes<0))Error("file-size","File size must be a nonnegative integer.");
    foreach(var property in new[]{"duration","frame_rate"})if(root.TryGetProperty(property,out var value)&&(!value.TryGetDoubleSafe(out var fileNumber)||!double.IsFinite(fileNumber)||fileNumber<0))Error("file-number",property+" must be a finite nonnegative number.");
    foreach(var property in new[]{"width","height","bitrate"})if(root.TryGetProperty(property,out var value)&&(!value.TryGetInt64Safe(out var integer)||integer<0||property!="bitrate"&&integer>int.MaxValue))Error("file-number",property+" must be a supported nonnegative integer.");
    _=String(root,"zip_file");break;
   case StashEntityKind.SavedFilter:Warning("saved-filter","Stash saved filters are retained as provenance and not executed as FlixyPix queries.");break;
  }
  return(kind,key,issues);
 }
 private static void Unique(JsonElement root,Action<string,string> error)
 {
  if(root.ValueKind==JsonValueKind.Object)
  {
   var seen=new HashSet<string>(StringComparer.Ordinal);
   foreach(var property in root.EnumerateObject()){if(!seen.Add(property.Name))error("duplicate-property","Duplicate JSON property: "+property.Name);Unique(property.Value,error);}
  }
  else if(root.ValueKind==JsonValueKind.Array)foreach(var item in root.EnumerateArray())Unique(item,error);
 }
 private static bool TryGetInt32Safe(this JsonElement value,out int result){result=0;return value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out result);}
 private static bool TryGetInt64Safe(this JsonElement value,out long result){result=0;return value.ValueKind==JsonValueKind.Number&&value.TryGetInt64(out result);}
 private static bool TryGetDoubleSafe(this JsonElement value,out double result){result=0;return value.ValueKind==JsonValueKind.Number&&value.TryGetDouble(out result);}
}
