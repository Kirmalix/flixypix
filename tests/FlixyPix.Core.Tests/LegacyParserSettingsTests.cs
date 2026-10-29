using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class LegacyParserSettingsTests : IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-parser-settings-"+Guid.NewGuid().ToString("N"));
 public LegacyParserSettingsTests()=>Directory.CreateDirectory(root);
 public void Dispose()=>Directory.Delete(root,true);

 [Fact]
 public void KnownRecordInventoriesFieldsAndProposesOnlyInactiveTokenization()
 {
  var bytes=Payload();var draft=LegacyObjectReader.ReadParserSettings(bytes,"source-a","17");
  Assert.Equal("MyMediaViewer.frmParseFilenameToTags+ParseFilenameSettings",draft.RootType);
  Assert.Equal(HistoricalProjectionStatus.Partial,draft.Status);Assert.False(draft.IsExecutable);
  Assert.Equal(10,draft.Strings.Count);Assert.Equal(12,draft.Flags.Count);Assert.Equal(7,draft.Numbers.Count);Assert.Empty(draft.UnknownMembers);
  Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)),draft.PayloadSha256);Assert.Equal(bytes,Convert.FromBase64String(draft.PayloadBase64));
  Assert.NotNull(draft.StandardTranslation);Assert.Equal(" ",draft.StandardTranslation.Separators);
  Assert.Equal(new[]{"One","Two"},CatalogRepository.ParseFilenameTags(Path.Combine(root,"One Two.jpg"),draft.StandardTranslation));
  Assert.Contains(draft.Warnings,text=>text.Contains("formatTag"));
  using var repository=new CatalogRepository(Path.Combine(root,"catalog.db"));repository.UpsertLegacyParserDraft(draft);
  Assert.Equal(draft.PayloadSha256,Assert.Single(repository.GetLegacyParserDrafts()).PayloadSha256);Assert.Empty(repository.GetPreferences().FilenameParsers);
  repository.UpsertLegacyParserDraft(LegacyObjectReader.ReadParserSettings(bytes,"source-a","17"));Assert.Single(repository.GetLegacyParserDrafts());
 }

 [Fact]
 public void RegexModeAndCategoryDictionaryStayInactiveAndRetainTemplates()
 {
  var draft=LegacyObjectReader.ReadParserSettings(Payload(activeTab:1,category:true),"source-a","18");
  Assert.Null(draft.StandardTranslation);Assert.False(draft.IsExecutable);Assert.Equal(1,draft.Numbers["_activeTab"]);
  var template=Assert.Single(draft.CategoryTemplates);Assert.Equal("People",template.Category);Assert.Equal("$2",template.Template);Assert.Equal(13,template.PropertyId);Assert.True(template.IsProperty);
  Assert.Equal("(a+)+$",draft.Strings["_regexTemplate"]); // It is data, never compiled or run.
 }

 [Theory]
 [InlineData(99,false)]
 [InlineData(0,true)]
 public void UnknownEnumsAndFieldsAreReportedWithoutDroppingThePayload(int activeTab,bool unknownMember)
 {
  var bytes=Payload(activeTab:activeTab,unknownMember:unknownMember);var draft=LegacyObjectReader.ReadParserSettings(bytes,"source-a","19");
  Assert.False(draft.IsExecutable);Assert.Null(draft.StandardTranslation);Assert.Equal(bytes,Convert.FromBase64String(draft.PayloadBase64));
  if(unknownMember){Assert.Contains("_futureOption",draft.UnknownMembers);Assert.Contains(draft.Warnings,text=>text.Contains("Unknown parser member"));}
  else{Assert.Equal(99,draft.Numbers["_activeTab"]);Assert.Contains(draft.Warnings,text=>text.Contains("Unknown parser code"));}
 }

 [Fact]
 public void MalformedOrForeignRecordsBecomeOpaqueAndOversizedPayloadIsRejected()
 {
  var bytes=Payload();var truncated=LegacyObjectReader.ReadParserSettings(bytes[..^3],"source-a","20");
  Assert.Equal(HistoricalProjectionStatus.Opaque,truncated.Status);Assert.False(truncated.IsExecutable);Assert.NotEmpty(truncated.Warnings);
  var foreign=LegacyObjectReader.ReadParserSettings(Payload(foreign:true),"source-a","21");Assert.Equal(HistoricalProjectionStatus.Opaque,foreign.Status);Assert.Empty(foreign.Flags);Assert.Null(foreign.StandardTranslation);
  Assert.Throws<InvalidDataException>(()=>LegacyObjectReader.ReadParserSettings(new byte[LegacyObjectReader.MaximumPayloadBytes+1],"source-a","22"));
  var oneByte=LegacyObjectReader.ReadParserSettings([0],"source-a","23");Assert.Equal(HistoricalProjectionStatus.Opaque,oneByte.Status);Assert.Equal(new byte[]{0},Convert.FromBase64String(oneByte.PayloadBase64));
 }

 [Fact]
 public void StoredPayloadIntegrityIsValidatedAndSourceFilesRemainUnchanged()
 {
  var bytes=Payload();var path=Path.Combine(root,"parser.bin");File.WriteAllBytes(path,bytes);
  var draft=LegacyObjectReader.ReadParserSettings(path,"source-a","24");Assert.Equal(bytes,File.ReadAllBytes(path));
  using var repository=new CatalogRepository(Path.Combine(root,"catalog.db"));
  Assert.Throws<InvalidDataException>(()=>repository.UpsertLegacyParserDraft(draft with {PayloadSha256=new string('0',64)}));
  Assert.Throws<ArgumentException>(()=>repository.UpsertLegacyParserDraft(draft with {Status=HistoricalProjectionStatus.Complete}));
  repository.UpsertLegacyParserDraft(draft);Assert.Equal(draft.Id,Assert.Single(repository.GetLegacyParserDrafts()).Id);
 }

 [Theory]
 [InlineData(false)]
 [InlineData(true)]
 public void LegacyImportAndPortableRoundtripKeepInactiveDraftAndOriginalBytes(bool sidecar)
 {
  var bytes=Payload(activeTab:1,category:true);var path=Path.Combine(root,"legacy.jsonl");var hash=Convert.ToHexStringLower(SHA256.HashData(bytes));
  object binary=sidecar?new Dictionary<string,object>{{"$binaryRef",new {sha256=hash,length=bytes.Length}}}:new Dictionary<string,object>{{"$binary",Convert.ToBase64String(bytes)}};
  if(sidecar){Directory.CreateDirectory(path+".blobs");File.WriteAllBytes(Path.Combine(path+".blobs",hash+".bin"),bytes);}
  object[] records=[new {type="header",format="flixypix-legacy",version=sidecar?2:1,sourceSha256=new string('d',64)},
   new {type="schema",table="configsettings",columns=new[]{"ID","parameter","blobobject"}.Select(name=>new{name})},
   new {type="row",table="configsettings",values=new {ID=17,parameter="parsefilename",blobobject=binary}},
   new {type="footer",tables=1,rows=1,tableRows=new {configsettings=1},blobCount=sidecar?1:0,blobBytes=sidecar?bytes.Length:0}];
  File.WriteAllLines(path,records.Select(record=>JsonSerializer.Serialize(record)));
  using var repository=new CatalogRepository(Path.Combine(root,"catalog.db"));var result=repository.ImportLegacyJsonl(path,new LegacyImportOptions{SourceId="source-a"});Assert.Empty(result.Errors);
  var draft=Assert.Single(repository.GetLegacyParserDrafts());Assert.False(draft.IsExecutable);Assert.Equal(bytes,Convert.FromBase64String(draft.PayloadBase64));Assert.Single(draft.CategoryTemplates);
  var again=repository.ImportLegacyJsonl(path,new LegacyImportOptions{SourceId="source-a"});Assert.Empty(again.Errors);Assert.Single(repository.GetLegacyParserDrafts());
  var portable=Path.Combine(root,"portable.json");repository.Export(portable);
  using var restored=new CatalogRepository(Path.Combine(root,"restored.db"));Assert.Empty(restored.ImportJson(portable).Errors);
  var copy=Assert.Single(restored.GetLegacyParserDrafts());Assert.Equal(draft.PayloadSha256,copy.PayloadSha256);Assert.Equal(draft.Id,copy.Id);Assert.False(copy.IsExecutable);Assert.Empty(restored.GetPreferences().FilenameParsers);
 }

 // Handwritten MS-NRBF records referencing an assembly which is not present.
 // No BinaryFormatter or serialization of a runtime object is used.
 private static byte[] Payload(int activeTab=0,bool category=false,bool unknownMember=false,bool foreign=false)
 {
  const string parser="MyMediaViewer.frmParseFilenameToTags+ParseFilenameSettings";
  var strings=new[]{"_removeCharacters","_replaceWithSpace","_tagExclusions","_regexTagExclusions","_regexExcludeByToken","_regexTemplate","_titleTemplate","_regex_removeCharacters","_regex_replaceWithSpace","_regex_dateformat"};
  var flags=new[]{"_parseFilename","_parsePath","_applyExToFilename","_applyExToPath","_applyTagSepToFilename","_applyTagSepToPath","_matchExisting","_useRegex","_regexIncludePath","_regexCaseSensitive","_regexIncludeExtension","_regexMatchExisting"};
  var numbers=new[]{"_tagSeparator","_folderLevel","_standardMatchOption","_regexMatchOption","_activeTab","_mode","_regexFormulaOption"};
  var members=strings.Concat(flags).Concat(numbers).Append("_categoryTemplates").Concat(unknownMember?["_futureOption"]:Array.Empty<string>()).ToArray();
  var kinds=strings.Select(_=>(byte)1).Concat(flags.Select(_=>(byte)0)).Concat(numbers.Select(name=>name=="_activeTab"?(byte)2:(byte)0)).Append((byte)2).Concat(unknownMember?[(byte)1]:Array.Empty<byte>()).ToArray();
  using var stream=new MemoryStream();using var writer=new BinaryWriter(stream,Encoding.UTF8,leaveOpen:true);
  writer.Write((byte)0);writer.Write(1);writer.Write(-1);writer.Write(1);writer.Write(0);
  writer.Write((byte)12);writer.Write(99);writer.Write("Absent.Original.Assembly, Version=4.8.4.0, Culture=neutral, PublicKeyToken=null");
  Class(writer,1,foreign?"Unknown.Parser":parser,members,kinds,[..flags.Select(_=>(byte)1),..numbers.Where(name=>name!="_activeTab").Select(_=>(byte)8)],true);
  var id=10;
  foreach(var name in strings)String(writer,id++,name=="_regexTemplate"?"(a+)+$":name=="_regex_dateformat"?"yyyy-MM-dd":"");
  foreach(var name in flags)writer.Write(name is "_parseFilename" or "_applyTagSepToFilename");
  foreach(var name in numbers)
  {
   if(name=="_activeTab"){Class(writer,id++,"MyMediaViewer.frmParseFilenameToTags+ParseFilenameTabs",["value__"],[0],[8],true);writer.Write(activeTab);}
   else writer.Write(0);
  }
  if(!category)writer.Write((byte)10);
  else
  {
   var template=parser+"+regexCategoryTemplate";var args="[[System.Int32, mscorlib],["+template+", Absent.Original.Assembly]]";
   Class(writer,id++,"System.Collections.Generic.Dictionary`2"+args,["HashSize","KeyValuePairs"],[0,2],[8]);writer.Write(1);
   writer.Write((byte)7);writer.Write(id++);writer.Write((byte)0);writer.Write(1);writer.Write(1);writer.Write((byte)3);writer.Write("System.Collections.Generic.KeyValuePair`2"+args);
   Class(writer,id++,"System.Collections.Generic.KeyValuePair`2"+args,["key","value"],[0,2],[8]);writer.Write(2);
   Class(writer,id++,template,["category","template","isDefault","property_id","isproperty"],[1,1,0,0,0],[1,8,1],true);
   String(writer,id++,"People");String(writer,id++,"$2");writer.Write(false);writer.Write(13);writer.Write(true);
  }
  if(unknownMember)String(writer,id++,"retain this unknown setting");writer.Write((byte)11);writer.Flush();return stream.ToArray();
 }
 private static void Class(BinaryWriter writer,int id,string type,string[] members,byte[] kinds,byte[] primitives,bool library=false)
 {writer.Write(library?(byte)5:(byte)4);writer.Write(id);writer.Write(type);writer.Write(members.Length);foreach(var member in members)writer.Write(member);writer.Write(kinds);writer.Write(primitives);if(library)writer.Write(99);}
 private static void String(BinaryWriter writer,int id,string value){writer.Write((byte)6);writer.Write(id);writer.Write(value);}
}
