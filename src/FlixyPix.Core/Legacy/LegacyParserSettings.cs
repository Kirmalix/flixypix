using System.Formats.Nrbf;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed record LegacyParserCategoryTemplate(int Ordinal,string? Category,string? Template,bool IsDefault,int PropertyId,bool IsProperty);

/// <summary>Inactive structural inventory. StandardTranslation is a partial tokenization proposal, never an activated parser.</summary>
public sealed record LegacyParserDraft
{
 public Guid Id { get; init; }
 public string SourceId { get; init; }="";
 public string LegacyId { get; init; }="";
 public string? RootType { get; init; }
 public HistoricalProjectionStatus Status { get; init; }=HistoricalProjectionStatus.Opaque;
 public string PayloadSha256 { get; init; }="";
 public string PayloadBase64 { get; init; }="";
 public Dictionary<string,string?> Strings { get; init; }=new(StringComparer.Ordinal);
 public Dictionary<string,bool> Flags { get; init; }=new(StringComparer.Ordinal);
 public Dictionary<string,int> Numbers { get; init; }=new(StringComparer.Ordinal);
 public List<string> UnknownMembers { get; init; }=[];
 public List<LegacyParserCategoryTemplate> CategoryTemplates { get; init; }=[];
 public List<string> Warnings { get; init; }=[];
 public FilenameParserPreset? StandardTranslation { get; init; }
 public bool IsExecutable=>false;
}

public static partial class LegacyObjectReader
{
 private const string ParserType="MyMediaViewer.frmParseFilenameToTags+ParseFilenameSettings";
 private const string ParserTabType="MyMediaViewer.frmParseFilenameToTags+ParseFilenameTabs";
 private const string ParserTemplateType=ParserType+"+regexCategoryTemplate";
 private static readonly string[] ParserStrings=["_removeCharacters","_replaceWithSpace","_tagExclusions","_regexTagExclusions","_regexExcludeByToken","_regexTemplate","_titleTemplate","_regex_removeCharacters","_regex_replaceWithSpace","_regex_dateformat"];
 private static readonly string[] ParserFlags=["_parseFilename","_parsePath","_applyExToFilename","_applyExToPath","_applyTagSepToFilename","_applyTagSepToPath","_matchExisting","_useRegex","_regexIncludePath","_regexCaseSensitive","_regexIncludeExtension","_regexMatchExisting"];
 private static readonly string[] ParserNumbers=["_tagSeparator","_folderLevel","_standardMatchOption","_regexMatchOption","_activeTab","_mode","_regexFormulaOption"];

 public static LegacyParserDraft ReadParserSettings(string path,string sourceId,string legacyId)
 {
  using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
  if(input.Length is <1 or >MaximumPayloadBytes)throw Invalid("Parser payload must be between 1 byte and 32 MiB; larger source bytes must remain preserved separately.");
  var bytes=new byte[(int)input.Length];input.ReadExactly(bytes);return ReadParserSettings(bytes,sourceId,legacyId);
 }

 public static LegacyParserDraft ReadParserSettings(byte[] payload,string sourceId,string legacyId)
 {
  ArgumentNullException.ThrowIfNull(payload);LegacyHistoricalReader.RequireIdentity(sourceId,legacyId);
  if(payload.Length is <1 or >MaximumPayloadBytes)throw Invalid("Parser payload must be between 1 byte and 32 MiB.");
  var hash=Convert.ToHexStringLower(SHA256.HashData(payload));
  var draft=new LegacyParserDraft {Id=LegacyHistoricalReader.HistoricalId(sourceId,"parsefilename",legacyId),SourceId=sourceId,LegacyId=legacyId,PayloadSha256=hash,PayloadBase64=Convert.ToBase64String(payload)};
  var warnings=new List<string>{"Inactive structural draft; no original type, constructor, callback, regex or media operation was executed."};
  try
  {
   var decoded=ReadBytes(payload);draft=draft with {RootType=decoded.Root.TypeName.FullName};
   if(decoded.Root is not ClassRecord root||root.TypeName.FullName!=ParserType)
    return draft with {Warnings=[..warnings,"Unknown parser root retained without projection."]};
   if(root.MemberNames.Count()>128)throw Invalid("Parser root exceeds 128 members.");
   var strings=new Dictionary<string,string?>(StringComparer.Ordinal);var flags=new Dictionary<string,bool>(StringComparer.Ordinal);var numbers=new Dictionary<string,int>(StringComparer.Ordinal);
   var known=ParserStrings.Concat(ParserFlags).Concat(ParserNumbers).Append("_categoryTemplates").ToHashSet(StringComparer.Ordinal);
   var unknown=root.MemberNames.Where(member=>!known.Contains(member)).ToList();
   foreach(var member in unknown)warnings.Add("Unknown parser member preserved in payload: "+member);
   foreach(var member in ParserStrings)ReadMember(member,()=>{var text=root.GetString(member);if(text?.Length>16384)throw Invalid("Oversized parser string.");strings.Add(member,text);});
   foreach(var member in ParserFlags)ReadMember(member,()=>flags.Add(member,root.GetBoolean(member)));
   foreach(var member in ParserNumbers)ReadMember(member,()=>numbers.Add(member,member=="_activeTab"?EnumValue(root,member,ParserTabType):root.GetInt32(member)));
   var templates=new List<LegacyParserCategoryTemplate>();
   if(root.HasMember("_categoryTemplates"))
   {
    try{ReadParserTemplates(root.GetClassRecord("_categoryTemplates"),templates,warnings);}
    catch(Exception error)when(error is InvalidDataException or InvalidOperationException or KeyNotFoundException or ArgumentException)
    {warnings.Add("Category templates remain raw: "+error.Message);}
   }
   else warnings.Add("Missing parser member: _categoryTemplates");
   CheckEnum("_activeTab",0,2);CheckEnum("_tagSeparator",0,5);CheckEnum("_folderLevel",0,5);CheckEnum("_standardMatchOption",0,2);CheckEnum("_regexMatchOption",0,2);CheckEnum("_mode",0,1);CheckEnum("_regexFormulaOption",0,1);
   if(flags.GetValueOrDefault("_regexIncludeExtension"))warnings.Add("Stored _regexIncludeExtension=true is retained, but the original property getter always returns false; no effective behavior is inferred.");
   draft=draft with {Status=HistoricalProjectionStatus.Partial,Strings=strings,Flags=flags,Numbers=numbers,UnknownMembers=unknown,CategoryTemplates=templates};
   // Only the proven filename tokenization subset maps to the new literal parser.
   // Original formatTag/global substitutions remain outside this candidate, so it stays partial and inactive.
   if(warnings.Count==1&&numbers.GetValueOrDefault("_activeTab",-1)==0&&numbers.GetValueOrDefault("_mode",-1)==0&&numbers.GetValueOrDefault("_standardMatchOption",-1)==0&&
      flags.GetValueOrDefault("_parseFilename")&&!flags.GetValueOrDefault("_parsePath")&&!flags.GetValueOrDefault("_useRegex")&&!flags.GetValueOrDefault("_matchExisting")&&
      flags.GetValueOrDefault("_applyTagSepToFilename")&&numbers.TryGetValue("_tagSeparator",out var separator)&&separator is >=0 and <=4&&
      string.IsNullOrWhiteSpace(strings.GetValueOrDefault("_removeCharacters"))&&string.IsNullOrWhiteSpace(strings.GetValueOrDefault("_replaceWithSpace"))&&string.IsNullOrEmpty(strings.GetValueOrDefault("_tagExclusions"))&&templates.Count==0)
   {
    draft=draft with {StandardTranslation=new FilenameParserPreset{Id=LegacyHistoricalReader.HistoricalId(sourceId,"parsefilename-tokenization",legacyId),Name="Legacy filename tokenization "+legacyId,Separators=new[]{" ",",",";","_","-"}[separator],ParentFolderDepth=0}};
    warnings.Add("StandardTranslation covers filename tokenization only. Original formatTag/global formatting is not reproduced; this is not an executable or parity-certified preset.");
   }
   else warnings.Add("Current parser mode or options have no complete translation to the new filename parser; retained as an inactive draft.");
   return draft with {Warnings=warnings.Distinct(StringComparer.Ordinal).ToList()};

   void ReadMember(string member,Action read)
   {
    if(!root.HasMember(member)){warnings.Add("Missing parser member: "+member);return;}
    try{read();}catch(Exception error)when(error is InvalidDataException or InvalidOperationException or KeyNotFoundException or ArgumentException)
    {warnings.Add("Unsupported parser member preserved raw: "+member+" ("+error.GetType().Name+")");}
   }
   void CheckEnum(string member,int minimum,int maximum){if(numbers.TryGetValue(member,out var value)&&(value<minimum||value>maximum))warnings.Add("Unknown parser code preserved: "+member+"="+value);}
  }
  catch(InvalidDataException error){return draft with {Warnings=[..warnings,"Invalid or unsupported parser structure retained raw: "+error.Message]};}
 }

 private static void ReadParserTemplates(ClassRecord? dictionary,List<LegacyParserCategoryTemplate> templates,List<string> warnings)
 {
  if(dictionary==null)return;
  RequireClass(dictionary,"System.Collections.Generic.Dictionary`2",true);RequireGenericArguments(dictionary,"System.Int32",ParserTemplateType);
  var array=dictionary.GetSerializationRecord("KeyValuePairs") as SZArrayRecord<SerializationRecord>??throw Invalid("Category dictionary has no record array.");
  if(array.Lengths[0]>1000)throw Invalid("Category dictionary exceeds 1000 entries.");
  var ordinals=new HashSet<int>();
  foreach(var pairRecord in array.GetArray(allowNulls:false))
  {
   var pair=RequireClass(pairRecord,"System.Collections.Generic.KeyValuePair`2",true);RequireGenericArguments(pair,"System.Int32",ParserTemplateType);
   var ordinal=pair.GetInt32("key");if(ordinal<0||!ordinals.Add(ordinal))throw Invalid("Invalid or duplicate category ordinal.");
   var value=RequireClass(pair.GetClassRecord("value"),ParserTemplateType);
   foreach(var member in value.MemberNames.Where(member=>member is not ("category" or "template" or "isDefault" or "property_id" or "isproperty")))warnings.Add("Unknown category-template member retained raw: "+member);
   var category=value.GetString("category");var template=value.GetString("template");if(category?.Length>16384||template?.Length>16384)throw Invalid("Oversized category template string.");
   templates.Add(new(ordinal,category,template,value.GetBoolean("isDefault"),value.GetInt32("property_id"),value.GetBoolean("isproperty")));
  }
  templates.Sort((left,right)=>left.Ordinal.CompareTo(right.Ordinal));
 }
}

public sealed partial class CatalogRepository
{
 private void InitializeLegacyParserSchema(SqliteTransaction tx)
 {using var command=Command("CREATE TABLE IF NOT EXISTS legacy_parser_drafts(id TEXT PRIMARY KEY,document TEXT NOT NULL)",tx);command.ExecuteNonQuery();}

 public IReadOnlyList<LegacyParserDraft> GetLegacyParserDrafts()=>ReadHistoricalDocuments<LegacyParserDraft>("legacy_parser_drafts");

 public LegacyParserDraft UpsertLegacyParserDraft(LegacyParserDraft draft)
 {
  ValidateLegacyParserDraft(draft);
  lock(gate){ThrowIfDisposed();using var tx=connection.BeginTransaction();InitializeLegacyParserSchema(tx);SaveDocument("legacy_parser_drafts",draft.Id,draft,tx);tx.Commit();return draft;}
 }

 private void ImportLegacyParserSettings(Func<string,IEnumerable<JsonElement>> rows,string sourceId,SqliteTransaction tx,List<string> warnings,CancellationToken ct)
 {
  InitializeLegacyParserSchema(tx);var ordinal=0;
  foreach(var row in rows("configsettings"))
  {
   ct.ThrowIfCancellationRequested();ordinal++;
   if(!string.Equals(Text(row,"parameter"),"parsefilename",StringComparison.OrdinalIgnoreCase))continue;
   var key=Text(row,"ID")??"parsefilename:"+ordinal;
   try
   {
    var value=Property(row,"blobobject");var inline=Property(value,"$binary");
    if(inline.ValueKind==JsonValueKind.String&&inline.GetString()!.Length>(long)LegacyObjectReader.MaximumPayloadBytes*4/3+4)throw new InvalidDataException("Parser exceeds the 32 MiB structural limit; original bytes remain preserved.");
    var payload=ReadLegacyAssetBytes(value,tx);var draft=LegacyObjectReader.ReadParserSettings(payload,sourceId,key);ValidateLegacyParserDraft(draft);
    SaveDocument("legacy_parser_drafts",draft.Id,draft,tx);
    AuditLegacy("configsettings",LegacyRowKey("configsettings",row),draft.Status==HistoricalProjectionStatus.Opaque?"raw-retained":"partially-projected","inactive-parser-draft",tx,draft.Id);
    warnings.AddRange(draft.Warnings.Select(message=>"configsettings/"+key+": "+message));
   }
   catch(Exception error)when(error is InvalidDataException or FormatException or ArgumentException)
   {AuditLegacy("configsettings",LegacyRowKey("configsettings",row),"raw-retained","parser-payload-not-projected",tx);warnings.Add("configsettings/"+key+": parser payload remains raw: "+error.Message);}
  }
 }

 private void SavePortableLegacyParserDraft(JsonElement value,SqliteTransaction tx)
 {
  var draft=value.Deserialize<LegacyParserDraft>(Json)??throw new InvalidDataException("Null legacy parser draft.");
  ValidateLegacyParserDraft(draft);SaveDocument("legacy_parser_drafts",draft.Id,draft,tx);
 }

 private static void ValidateLegacyParserDraft(LegacyParserDraft draft)
 {
  ArgumentNullException.ThrowIfNull(draft);LegacyHistoricalReader.RequireIdentity(draft.SourceId,draft.LegacyId);
  if(draft.Id==Guid.Empty||!Enum.IsDefined(draft.Status)||draft.Status==HistoricalProjectionStatus.Complete||draft.RootType?.Length>16384||draft.PayloadSha256 is not {Length:64}||draft.PayloadSha256.Any(c=>!char.IsAsciiHexDigitLower(c))||
     draft.PayloadBase64==null||draft.PayloadBase64.Length>(long)LegacyObjectReader.MaximumPayloadBytes*4/3+4||draft.Strings==null||draft.Flags==null||draft.Numbers==null||draft.UnknownMembers==null||draft.CategoryTemplates==null||draft.Warnings==null||
     draft.Strings.Count>128||draft.Flags.Count>128||draft.Numbers.Count>128||draft.UnknownMembers.Count>128||draft.CategoryTemplates.Count>1000||draft.Warnings.Count>1000||
     draft.Strings.Any(pair=>pair.Key.Length>16384||pair.Value?.Length>16384)||draft.UnknownMembers.Any(text=>text==null||text.Length>16384)||draft.Warnings.Any(text=>text==null||text.Length>32768)||
     draft.CategoryTemplates.Any(template=>template==null||template.Ordinal<0||template.Category?.Length>16384||template.Template?.Length>16384))throw new ArgumentException("Invalid bounded inactive parser draft.");
  byte[] payload;try{payload=Convert.FromBase64String(draft.PayloadBase64);}catch(FormatException error){throw new InvalidDataException("Invalid parser payload base64.",error);}
  if(payload.Length is <1 or >LegacyObjectReader.MaximumPayloadBytes||Convert.ToHexStringLower(SHA256.HashData(payload))!=draft.PayloadSha256)throw new InvalidDataException("Parser payload hash mismatch.");
  if(draft.StandardTranslation!=null)ValidateParser(draft.StandardTranslation);
 }
}
