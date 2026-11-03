using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class StashProvenanceTests:IDisposable
{
 private readonly string root=StashTestWorkspace.Create("flixypix-stash-provenance-");
 private string Database=>Path.Combine(root,"catalog.db");
 private const string Secret="synthetic-stash-provenance-password";
 public StashProvenanceTests()=>Directory.CreateDirectory(root);
 public void Dispose(){SqliteConnection.ClearAllPools();if(Environment.GetEnvironmentVariable("FLIXYPIX_TEST_CAPTURE")!="1")Directory.Delete(root,true);}
 private CatalogKey? Prepare(bool encrypted)
 {using(var db=new CatalogRepository(Database)){}if(!encrypted)return null;CatalogEncryption.EnableEncryption(Database,Secret);return CatalogEncryption.Unlock(Database,Secret);}
 private (StashSourceProfile Source,StashIdentity Identity) Setup(CatalogRepository db)
 {
  var source=db.SaveStashSource(new(){Name="Source",SourceDirectory=Path.Combine(root,"source")});var identity=Assert.Single(db.StoreStashIdentities(source.Id,source.Revision,[new(new(){SourceId=source.Id,Kind=StashEntityKind.Scene,Keys=[new(StashIdentityKeyKind.MediaPath,"unix:/media/a.mp4")]},null)]));return(source,identity);
 }
 private static StashSourceDocument Raw(byte[] bytes,string path="scenes/a.json")=>new(1,StashEntityKind.Scene,path,Convert.ToHexStringLower(SHA256.HashData(bytes)),null,bytes,null,[]);
 private static StashSourceDocument Raw(string json,string path="scenes/a.json")=>Raw(Encoding.UTF8.GetBytes(json),path);
 private static StashProvenance Capture(CatalogRepository db,StashSourceProfile source,StashIdentity identity,StashSourceDocument document)
 =>Assert.Single(db.CaptureStashProvenance(source.Id,source.Revision,[new(identity.Id,identity.Revision,document)]));
 [Theory][InlineData(true)]
 public void OriginalBytesUnknownFieldsWhitespaceAndRenameHistorySurviveReopen(bool encrypted)
 {
  using var key=Prepare(encrypted);Guid identityId,headerId;var original=Raw("{\r\n  \"title\":\"PrivateRawSentinel123\", \"future\":{\"opaque\":[1,true,null]}\r\n}\r\n");
  using(var db=new CatalogRepository(Database,key))
  {
   var(source,identity)=Setup(db);identityId=identity.Id;var header=Capture(db,source,identity,original);headerId=header.Id;Assert.Equal(original.OriginalBytes,db.ReadStashProvenanceBytes(header.Id));Assert.Equal(header,Capture(db,source,identity,original));Assert.Single(db.GetStashProvenanceHistory(identity.Id));
   var renamed=Capture(db,source,identity,original with{RelativePath="scenes/renamed.json"});var changed=Capture(db,source,identity,Raw("{\"title\":\"Changed\"}","scenes/renamed.json"));Assert.NotEqual(header.Id,renamed.Id);Assert.NotEqual(renamed.Id,changed.Id);Assert.Equal(3,db.GetStashProvenanceHistory(identity.Id).Count);Assert.Equal(original.OriginalBytes,db.ReadStashProvenanceBytes(renamed.Id));
  }
  using var reopened=new CatalogRepository(Database,key);Assert.Equal(3,reopened.GetStashProvenanceHistory(identityId).Count);Assert.Equal(original.OriginalBytes,reopened.ReadStashProvenanceBytes(headerId));
  if(encrypted)foreach(var file in Directory.GetFiles(root,"catalog.db*"))
  {using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);var data=new byte[(int)stream.Length];stream.ReadExactly(data);Assert.False(data.AsSpan().IndexOf(Encoding.UTF8.GetBytes("PrivateRawSentinel123"))>=0);}
 }
 [Theory][InlineData("missing")][InlineData("duplicate")][InlineData("orphan")][InlineData("corrupt")][InlineData("index")][InlineData("length")][InlineData("header-id")][InlineData("identity")][InlineData("duplicate-header")]
 public void MalformedPortableProvenanceRollsBackAllEarlierWritesAndAllowsCleanRetry(string damage)
 {
  using var db=new CatalogRepository(Database);var(source,identity)=Setup(db);var raw=Raw("{\"title\":\"Source\"}");var header=Capture(db,source,identity,raw);var media=db.Upsert(new(){Path=Path.Combine(root,"photo.jpg"),Title="Original"});var clean=Path.Combine(root,"clean.json");db.Export(clean);var doc=JsonNode.Parse(File.ReadAllText(clean))!.AsObject();doc["items"]![0]!["title"]="Must roll back";var chunks=doc["stashProvenanceChunks"]!.AsArray();var headers=doc["stashProvenance"]!.AsArray();
  switch(damage)
  {
   case "missing":chunks.Clear();break;
   case "duplicate":chunks.Add(chunks[0]!.DeepClone());break;
   case "orphan":chunks[0]!["provenanceId"]=Guid.NewGuid();break;
   case "corrupt":chunks[0]!["data"]=Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('x',raw.OriginalBytes.Length)));break;
   case "index":chunks[0]!["index"]=1;break;
   case "length":headers[0]!["originalLength"]=raw.OriginalBytes.Length+1;break;
   case "header-id":headers[0]!["id"]=Guid.NewGuid();break;
   case "identity":headers[0]!["identityId"]=Guid.NewGuid();break;
   case "duplicate-header":headers.Add(headers[0]!.DeepClone());break;
   
  }
  var bad=Path.Combine(root,"bad.json");File.WriteAllText(bad,doc.ToJsonString());
  using var target=new CatalogRepository(Path.Combine(root,"empty.db"));
  Assert.NotEmpty(target.ImportJson(bad).Errors);Assert.Empty(target.Search());Assert.Empty(target.GetStashSources());Assert.Empty(target.GetStashIdentities(source.Id));Assert.Empty(target.GetStashProvenanceHistory(identity.Id));Assert.Null(target.GetStashBinding(identity.Id));
  if(damage is "corrupt" or "length")
  {
   Assert.NotEmpty(db.ImportJson(bad).Errors);Assert.Equal("Original",db.GetMedia(media.Id)!.Title);Assert.Equal(raw.OriginalBytes,db.ReadStashProvenanceBytes(header.Id));Assert.Equal(source.Name,db.GetStashSource(source.Id)!.Name);Assert.Equal(identity.Keys,db.GetStashIdentity(identity.Id)!.Keys);
  }
  if(damage is "duplicate" or "corrupt")
  {
   Assert.Empty(target.ImportJson(clean).Errors);Assert.Equal(raw.OriginalBytes,target.ReadStashProvenanceBytes(header.Id));Assert.Equal("Original",target.GetMedia(media.Id)!.Title);
  }
 }
 [Theory][InlineData(false)]
 public void HashMismatchAndLateCaptureFailureRollbackWholeBatch(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var(source,identity)=Setup(db);var first=Raw("{\"title\":\"First\"}");var second=Raw("{\"title\":\"Second\"}","scenes/b.json");second.OriginalBytes[0]=(byte)'!';
  Assert.Throws<InvalidDataException>(()=>db.CaptureStashProvenance(source.Id,source.Revision,[new(identity.Id,identity.Revision,first),new(identity.Id,identity.Revision,second)]));Assert.Empty(db.GetStashProvenanceHistory(identity.Id));Assert.NotNull(Capture(db,source,identity,first));
 }
 [Fact]
 public void StaleProfileIdentityWrongTypeAndCancellationNeverPublishRawBytes()
 {
  using var db=new CatalogRepository(Database);var(source,identity)=Setup(db);var raw=Raw("{}");var updated=Assert.Single(db.StoreStashIdentities(source.Id,source.Revision,[new(identity with{Keys=identity.Keys.Append(new(StashIdentityKeyKind.ExportPath,"scenes/alias.json")).ToList()},identity.Revision)]));
  Assert.Throws<InvalidOperationException>(()=>Capture(db,source,identity,raw));Assert.Throws<ArgumentException>(()=>Capture(db,source,updated,raw with{Kind=StashEntityKind.Image}));using var cancellation=new CancellationTokenSource();cancellation.Cancel();Assert.Throws<OperationCanceledException>(()=>db.CaptureStashProvenance(source.Id,source.Revision,[new(updated.Id,updated.Revision,raw)],cancellation.Token));db.SaveStashSource(source with{Name="Changed"},source.Revision);Assert.Throws<InvalidOperationException>(()=>Capture(db,source,updated,raw));Assert.Empty(db.GetStashProvenanceHistory(identity.Id));
 }
 [Fact][Trait("Category","Slow")]
 public void LargestSupportedRawRecordExchangesAsBoundedChunks()
 {
  using var db=new CatalogRepository(Database);var(source,identity)=Setup(db);var bytes=new byte[StashProvenance.MaximumBytes];Array.Fill(bytes,(byte)' ');bytes[0]=(byte)'{';bytes[1]=(byte)'}';var document=Raw(bytes);var header=Capture(db,source,identity,document);var portable=Path.Combine(root,"large.json");db.Export(portable);
  using var target=new CatalogRepository(Path.Combine(root,"large-target.db"));Assert.Empty(target.ImportJson(portable,2*1024*1024).Errors);Assert.Equal(document.Sha256,Convert.ToHexStringLower(SHA256.HashData(target.ReadStashProvenanceBytes(header.Id))));
 }
 [Fact]
 public void CorruptStoredChunksCannotBeReadOrExportedOverAnExistingFile()
 {
  Guid id;using(var db=new CatalogRepository(Database)){var(source,identity)=Setup(db);id=Capture(db,source,identity,Raw("{}")).Id;}
  using(var raw=new SqliteConnection(CatalogEncryption.ConnectionString(Database,null).ToString())){raw.Open();using var sql=raw.CreateCommand();sql.CommandText="UPDATE stash_provenance_chunks SET data=X'2121'";sql.ExecuteNonQuery();}
  using var reopened=new CatalogRepository(Database);Assert.Throws<InvalidDataException>(()=>reopened.ReadStashProvenanceBytes(id));var path=Path.Combine(root,"keep.json");File.WriteAllText(path,"Keep previous file");Assert.Throws<InvalidDataException>(()=>reopened.Export(path));Assert.Equal("Keep previous file",File.ReadAllText(path));Assert.Empty(Directory.GetFiles(root,"*.tmp"));
 }
}
