using System.Security.Cryptography;
using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;
public sealed class MigrationTests:IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-migration-"+Guid.NewGuid());
 public MigrationTests()=>Directory.CreateDirectory(root);
 public void Dispose()=>Directory.Delete(root,true);
 private string Write(string name,string snapshot,object row,params object[] extra)
 {
  var records=new List<object>{new{type="header",format="flixypix-legacy",version=1,sourceSha256=snapshot},new{type="schema",table="movies",columns=JsonSerializer.SerializeToElement(row).EnumerateObject().Select(p=>new{name=p.Name}).ToArray()},new{type="row",table="movies",values=row}};
  records.AddRange(extra);records.Add(new{type="footer",tables=1,rows=1,tableRows=new Dictionary<string,int>{["movies"]=1}});var path=Path.Combine(root,name);File.WriteAllLines(path,records.Select(r=>JsonSerializer.Serialize(r)));return path;
 }
 [Fact]public void RepeatedImportAndNewSnapshotKeepLocalEditsAndStableRelationships()
 {
  using var db=new CatalogRepository(Path.Combine(root,"catalog.db"));
  var first=Write("first.jsonl",new('a',64),new{ID=1,fullpath=@"U:\offline",filename="movie.mp4",title="Original",plays=0,views=32767,addedDate=new Dictionary<string,string>{{"$datetime","2019-01-02T03:04:05.0000000"}}});
  var initial=db.ImportLegacyJsonl(first,new
   LegacyImportOptions(){SourceId="preserved-library"});Assert.Empty(initial.Errors);Assert.NotNull(initial.Migration);Assert.True(initial.Migration.IntegrityVerified);Assert.True(db.CheckIntegrity().IsValid);var
   media=Assert.Single(db.Search());Assert.Equal(0,media.PlayCount);Assert.Equal(32767,media.ViewCount);Assert.Equal("2019-01-02T03:04:05.0000000",media.OriginalAddedAt);
  db.Upsert(media with{Title="My local title",Tags=["Local"],Rating=9});var repeated=db.ImportLegacyJsonl(first,new LegacyImportOptions(){SourceId="preserved-library"});Assert.True(repeated.Migration!.AlreadyImported);Assert.Equal("My local title",Assert.Single(db.Search()).Title);
  var second=Write("second.jsonl",new('b',64),new{ID=1,fullpath=@"U:\offline",filename="movie.mp4",title="Updated source",plays=1,views=32767,addedDate="2019-01-02T03:04:05"});
  var updated=db.ImportLegacyJsonl(second,new
   LegacyImportOptions(){SourceId="preserved-library"});Assert.Empty(updated.Errors);Assert.Equal(media.Id,Assert.Single(db.Search()).Id);Assert.Equal(9,Assert.Single(db.Search()).Rating);Assert.True(updated.Migration!.Conflicts>0);Assert.Contains(db.GetMigrationConflicts(updated.Migration.Batch),c=>c.ChangedFields.Contains("title"));
  var audit=Assert.Single(db.GetMigrationRows(updated.Migration.Batch));Assert.Equal("partially-projected",audit.Status);Assert.Contains(media.Id.ToString(),audit.TargetIds);
  var output=Path.Combine(root,"audit.json");db.ExportMigrationReport(updated.Migration.Batch,output);using var report=JsonDocument.Parse(File.ReadAllText(output));Assert.Equal(1,report.RootElement.GetProperty("rows").GetArrayLength());
  var preview=db.PreviewMigrationMediaConflict(updated.Migration.Batch,media.Id);Assert.Equal("Updated source",preview.Conflict.IncomingValues!["title"].GetString());
  var resolved=db.ResolveMigrationMediaConflict(updated.Migration.Batch,media.Id,["title"],preview.CurrentSha256);Assert.Equal("Updated source",resolved.Title);Assert.Equal(9,resolved.Rating);Assert.Contains("Local",resolved.Tags);
  Assert.Throws<InvalidOperationException>(()=>db.ResolveMigrationMediaConflict(updated.Migration.Batch,media.Id,["title"],preview.CurrentSha256));
 }
 [Fact]public void OfflinePathsCanBeMappedAndRoundtrippedWithoutMediaAccess()
 {
  using var db=new CatalogRepository(Path.Combine(root,"catalog.db"));var item=db.Upsert(new(){Path=@"U:\old\movie.mp4",Kind=MediaKind.Video});
  Assert.Equal(MediaAvailability.TemporarilyOffline,Assert.Single(db.CheckMediaAvailability()).Status);
  var plan=db.PlanPathMapping([new(@"U:\old",@"U:\new")]);Assert.Empty(plan.Conflicts);Assert.Equal(1,db.ApplyPathMappingPlan(plan));Assert.Equal(@"U:\new\movie.mp4",db.GetMedia(item.Id)!.Path);Assert.Equal(@"U:\old\movie.mp4",db.GetMedia(item.Id)!.Extra["migration.originalPath"]);
  Assert.Throws<InvalidOperationException>(()=>db.ApplyPathMappingPlan(plan));
 }
 [Fact]public void CollidingMappingIsRejectedAndCancellationLeavesAllPathsIntact()
 {
  using var db=new CatalogRepository(Path.Combine(root,"catalog.db"));var a=db.Upsert(new(){Path=@"U:\old\a.mp4"});db.Upsert(new(){Path=@"U:\new\a.mp4"});
  var plan=db.PlanPathMapping([new(@"U:\old",@"U:\new")]);Assert.NotEmpty(plan.Conflicts);Assert.Throws<ArgumentException>(()=>db.ApplyPathMappingPlan(plan));Assert.Equal(@"U:\old\a.mp4",db.GetMedia(a.Id)!.Path);
  using var cancel=new CancellationTokenSource();cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>db.PlanPathMapping([new(@"U:\old",@"U:\else")],cancel.Token));
 }
 private string BlobExport(int mebibytes,bool graphic=false)
 {
  var path=Path.Combine(root,Guid.NewGuid()+".jsonl");var dir=path+".blobs";Directory.CreateDirectory(dir);var tmp=Path.Combine(dir,"input.tmp");var buffer=new byte[1024*1024];for(var i=0;i<buffer.Length;i++)buffer[i]=(byte)(i%251);
  using(var file=File.Create(tmp))for(var i=0;i<mebibytes;i++)file.Write(buffer);string hash;using(var file=File.OpenRead(tmp))hash=Convert.ToHexStringLower(SHA256.HashData(file));File.Move(tmp,Path.Combine(dir,hash+".bin"));
  var table=graphic?"videoscenes":"future_binary";object[] records=[new{type="header",format="flixypix-legacy",version=2,sourceSha256=new
   string('c',64)},new{type="schema",table,columns=new[]{new{name="ID"},new{name="img"}}},new{type="row",table,values=new{ID=1,img=new
   Dictionary<string,object>{{"$binaryRef",new{sha256=hash,length=(long)mebibytes*1024*1024}}}}},new{type="footer",tables=1,rows=1,tableRows=new
   Dictionary<string,int>{{table,1}},blobCount=1,blobBytes=(long)mebibytes*1024*1024}];File.WriteAllLines(path,records.Select(r=>JsonSerializer.Serialize(r)));return
   path;
 }
 [Fact]public async Task LargePreservedBlobSurvivesPortableAndBackupRoundtripWithoutDisplayLimit()
 {
  var path=BlobExport(33);using var db=new CatalogRepository(Path.Combine(root,"catalog.db"));var result=db.ImportLegacyJsonl(path);Assert.Empty(result.Errors);Assert.Equal(33L*1024*1024,result.Migration!.BlobBytes);Assert.Equal(1,result.Migration.Rows);
  var hash=Path.GetFileNameWithoutExtension(Assert.Single(Directory.GetFiles(path+".blobs")));var raw=Path.Combine(root,"raw.bin");db.ExportLegacyBlob(hash,raw);Assert.Equal(33L*1024*1024,new FileInfo(raw).Length);
  var exported=Path.Combine(root,"portable.json");db.Export(exported);using var target=new
   CatalogRepository(Path.Combine(root,"target.db"));Assert.Empty(target.ImportJson(exported).Errors);Assert.Single(target.GetMigrationReports());var
   raw2=Path.Combine(root,"restored.bin");target.ExportLegacyBlob(hash,raw2);Assert.Equal(File.ReadAllBytes(raw),File.ReadAllBytes(raw2));
  var backup=Path.Combine(root,"backup.flixypixbackup");await target.CreateBackupAsync(backup,false);var restore=Path.Combine(root,"restored");await
   CatalogRepository.RestoreBackupAsync(backup,restore);using var restored=new
   CatalogRepository(Path.Combine(restore,"catalog.db"));Assert.Single(restored.GetMigrationReports());restored.ExportLegacyBlob(hash,Path.Combine(root,"from-backup.bin"));
 }
 [Theory][InlineData("missing")][InlineData("wrong-hash")][InlineData("footer")]
 public void InvalidBlobPackageRollsBackAndCanBeRetried(string fault)
 {
  var path=BlobExport(1);var blob=Assert.Single(Directory.GetFiles(path+".blobs"));var bytes=File.ReadAllBytes(blob);var source=File.ReadAllText(path);
  if(fault=="missing")File.Delete(blob);else if(fault=="wrong-hash")File.WriteAllBytes(blob,new byte[bytes.Length]);else File.WriteAllText(path,source.Replace("\"blobCount\":1","\"blobCount\":2"));
  using var db=new CatalogRepository(Path.Combine(root,"catalog.db"));Assert.NotEmpty(db.ImportLegacyJsonl(path).Errors);Assert.Empty(db.GetMigrationReports());
  File.WriteAllBytes(blob,bytes);File.WriteAllText(path,source);Assert.Empty(db.ImportLegacyJsonl(path).Errors);Assert.Single(db.GetMigrationReports());
 }
 // The real private catalog (122k media, 185k rows) exposed a per-media full stage scan in the description
 // lookup. With an unusable index this case visits ~1.8 billion staged rows; indexed it takes seconds.
 [Fact]public void DescriptionLookupStaysIndexedOnLargeCatalogs()
 {
  const int pictures=30000;var source=Path.Combine(root,"large-descriptions.jsonl");
  using(var output=new StreamWriter(source))
  {
   output.WriteLine(JsonSerializer.Serialize(new{type="header",format="flixypix-legacy",version=2,sourceSha256=new string('c',64)}));
   output.WriteLine(JsonSerializer.Serialize(new{type="schema",table="pictures",columns=new[]{new{name="ID"},new{name="fullpath"},new{name="filename"}}}));
   for(var id=0;id<pictures;id++)output.WriteLine(JsonSerializer.Serialize(new{type="row",table="pictures",values=new{ID=id,fullpath=@"U:\offline",filename=$"image-{id:D6}.jpg"}}));
   output.WriteLine(JsonSerializer.Serialize(new{type="schema",table="descriptions",columns=new[]{new{name="ID"},new{name="mtype"},new{name="remoteid"},new{name="description"}}}));
   output.WriteLine(JsonSerializer.Serialize(new{type="row",table="descriptions",values=new{ID=1,mtype=9,remoteid=17,description="Beschreibung aus dem Original"}}));
   output.WriteLine(JsonSerializer.Serialize(new{type="schema",table="searchcriteria",columns=new[]{new{name="ID"},new{name="movie"},new{name="value1"}}}));
   for(var id=0;id<pictures;id++)output.WriteLine(JsonSerializer.Serialize(new{type="row",table="searchcriteria",values=new{ID=id,movie="SEARCHTAGINFO",value1=$"tag-{id}"}}));
   output.WriteLine(JsonSerializer.Serialize(new{type="footer",tables=3,rows=pictures*2+1,tableRows=new Dictionary<string,int>{{"pictures",pictures},{"descriptions",1},{"searchcriteria",pictures}},blobCount=0,blobBytes=0}));
  }
  var timer=System.Diagnostics.Stopwatch.StartNew();using var db=new CatalogRepository(Path.Combine(root,"large-descriptions.db"));var result=db.ImportLegacyJsonl(source);timer.Stop();
  Assert.Empty(result.Errors);Assert.Equal(pictures,db.GetFacets().MediaCount);
  Assert.Contains(db.Search(),item=>item.Description=="Beschreibung aus dem Original");
  Assert.True(timer.Elapsed<TimeSpan.FromSeconds(60),$"Legacy import of {pictures} pictures took {timer.Elapsed}.");
 }
 [Fact]public void MigrationMemoryEnvelopePreserves462MiBAndTenThousandOfflineMedia()
 {
  var source=BlobExport(462);var lines=File.ReadAllLines(source);const int mediaCount=10000;
  using(var output=new StreamWriter(source))
  {
   foreach(var line in lines[..^1])output.WriteLine(line);
   output.WriteLine(JsonSerializer.Serialize(new{type="schema",table="movies",columns=new[]{new{name="ID"},new{name="fullpath"},new{name="filename"}}}));
   for(var id=0;id<mediaCount;id++)output.WriteLine(JsonSerializer.Serialize(new{type="row",table="movies",values=new{ID=id,fullpath=@"U:\offline",filename=$"movie-{id:D6}.mp4"}}));
   output.WriteLine(JsonSerializer.Serialize(new{type="footer",tables=2,rows=mediaCount+1,tableRows=new Dictionary<string,int>{{"movies",mediaCount},{"future_binary",1}},blobCount=1,blobBytes=462L*1024*1024}));
  }
  var timer=System.Diagnostics.Stopwatch.StartNew();using var db=new CatalogRepository(Path.Combine(root,"load.db"));var result=db.ImportLegacyJsonl(source);timer.Stop();
  Assert.Empty(result.Errors);Assert.Equal(mediaCount,db.GetFacets().MediaCount);Assert.Equal(mediaCount+1,result.Migration!.Rows);Assert.Equal(462L*1024*1024,result.Migration.BlobBytes);
  using var process=System.Diagnostics.Process.GetCurrentProcess();process.Refresh();var peak=process.PeakWorkingSet64;Assert.True(peak<2L*1024*1024*1024,$"Migration test-host peak working set exceeded 2 GiB: {peak}");
  var project=new DirectoryInfo(AppContext.BaseDirectory);while(project!=null&&!File.Exists(Path.Combine(project.FullName,"FlixyPix.slnx")))project=project.Parent;
  if(project!=null){var
   directory=Path.Combine(project.FullName,"artifacts","test-results");Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"migration-memory-benchmark.json"),JsonSerializer.Serialize(new{blobBytes=result.Migration.BlobBytes,mediaCount,elapsedMs=timer.Elapsed.TotalMilliseconds,testHostPeakWorkingSetBytes=peak,privateSourcesUsed=false,mediaFilesAccessed=false}));}
 }
 [Theory][InlineData("overlap")][InlineData("missing")][InlineData("checksum")][InlineData("absent-payload")]
 public void PortableBlobDamageRollsBackAndRetainsExistingCatalog(string variant)
 {
  var source=BlobExport(2);using var db=new CatalogRepository(Path.Combine(root,"source.db"));Assert.Empty(db.ImportLegacyJsonl(source).Errors);
  var export=Path.Combine(root,"portable.json");db.Export(export);var document=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(export))!;var chunks=document["legacyBlobs"]!.AsArray();
  if(variant=="overlap")chunks[1]!["offset"]=1;else if(variant=="missing")chunks.RemoveAt(1);else if(variant=="checksum")chunks[0]!["data"]=Convert.ToBase64String(new byte[1024*1024]);else chunks.Clear();
  File.WriteAllText(export,document.ToJsonString());using var target=new CatalogRepository(Path.Combine(root,"target.db"));var existing=target.Upsert(new(){Path=@"U:\offline\existing.mp4"});
  Assert.NotEmpty(target.ImportJson(export).Errors);Assert.Equal(existing.Id,Assert.Single(target.QueryPage(new()).Items).Id);Assert.Empty(target.GetMigrationReports());Assert.True(target.CheckIntegrity().IsValid);
 }
 [Fact]public void EmptyBlobCanBePreservedAndTransported()
 {
  var source=BlobExport(0);using var db=new CatalogRepository(Path.Combine(root,"source.db"));var result=db.ImportLegacyJsonl(source);Assert.Empty(result.Errors);Assert.Equal(1,result.Migration!.Blobs);Assert.Equal(0,result.Migration.BlobBytes);
  var export=Path.Combine(root,"empty.json");db.Export(export);using var target=new
   CatalogRepository(Path.Combine(root,"target.db"));Assert.Empty(target.ImportJson(export).Errors);var
   hash=Path.GetFileNameWithoutExtension(Assert.Single(Directory.GetFiles(source+".blobs")));var
   restored=Path.Combine(root,"empty.bin");target.ExportLegacyBlob(hash,restored);Assert.Equal(0,new FileInfo(restored).Length);
 }
 [Fact]public void NewSnapshotWithoutDataChangesDoesNotInventImportDateConflicts()
 {
  using var db=new CatalogRepository(Path.Combine(root,"catalog.db"));var row=new{ID=1,fullpath=@"U:\offline",filename="same.mp4",title="Unchanged"};
  Assert.Empty(db.ImportLegacyJsonl(Write("one.jsonl",new('a',64),row),new LegacyImportOptions{SourceId="same-source"}).Errors);var before=Assert.Single(db.QueryPage(new()).Items);
  var result=db.ImportLegacyJsonl(Write("two.jsonl",new('b',64),row),new
   LegacyImportOptions{SourceId="same-source"});Assert.Empty(result.Errors);Assert.Equal(0,result.Migration!.Conflicts);Assert.Equal(before.AddedAt,Assert.Single(db.QueryPage(new()).Items).AddedAt);Assert.Equal(2,db.GetMigrationReports().Count);
 }
}
