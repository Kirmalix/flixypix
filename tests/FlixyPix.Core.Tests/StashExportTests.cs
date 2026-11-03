using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class StashExportTests:IDisposable
{
 private readonly string root=StashTestWorkspace.Create("flixypix-stash-reader-");
 private readonly List<string> links=[];
 private string Source=>Path.Combine(root,"source");private string Stage=>Path.Combine(root,"stage");
 public StashExportTests()=>Directory.CreateDirectory(Source);
 public void Dispose(){foreach(var link in links)if(Directory.Exists(link))Directory.Delete(link);SqliteConnection.ClearAllPools();if(Environment.GetEnvironmentVariable("FLIXYPIX_TEST_CAPTURE")!="1")Directory.Delete(root,true);}
 private string Add(string relative,string json)
 {var path=Path.Combine(Source,relative.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,Encoding.UTF8.GetBytes(json));return path;}
 [Fact]
 public async Task ActualExportShapeIsReadAndRawUnknownFieldsArePreservedInEncryptedStaging()
 {
  var records=new Dictionary<string,string>
  {
   ["scenes/title.hash.json"]="""{"title":"SentinelPrivateSource12345","files":["/mnt/library/video.mp4"],"rating":95,"organized":true,"performers":["A"],"studio":"Studio","tags":["Tag"],"markers":[{"seconds":"1.25","end_seconds":"2.75","primary_tag":"Tag"},{"seconds":"9"}],"movies":[{"movieName":"Group","scene_index":2}],"custom_fields":{"unmapped":"preserve"}}""",
   ["images/photo.hash.json"]="""{"files":["/mnt/library/image.jpg"],"galleries":[{"folder_path":"/mnt/library"}]}""",
   ["galleries/gallery.hash.json"]="""{"folder_path":"/mnt/library","title":"Gallery","chapters":[{"title":"Chapter","image_index":0}]}""",
   ["performers/A.json"]="""{"name":"A","height":"170","aliases":"Alias one,Alias two","disambiguation":"different"}""",
   ["studios/Studio.json"]="""{"name":"Studio","parent_studio":"Network"}""",
   ["tags/Tag.json"]="""{"name":"Tag","parents":["Parent"],"aliases":["Alias"]}""",
   ["movies/Group.json"]="""{"name":"Group","sub_groups":[{"name":"Part","description":"Keep"}]}""",
   ["files/video.hash.json"]="""{"type":"video","path":"/mnt/library/video.mp4","size":99,"duration":12.5,"frame_rate":23.976}""",
   ["files/image.hash.json"]="""{"type":"image","path":"/mnt/library/image.jpg","size":3}""",
   ["files/folder.hash.json"]="""{"type":"folder","path":"/mnt/library"}""",
   ["saved_filters/filter.json"]="""{"name":"Filter","filter":{"anything":true}}"""
  };
  foreach(var(path,json)in records)Add(path,json);
  using(var snapshot=await StashExport.ReadDirectoryAsync(Source,Stage))
  {
   Assert.Equal(11,snapshot.Count);Assert.Equal(0,snapshot.ErrorDocuments);var rows=snapshot.ReadPage(0);Assert.Equal(11,rows.Count);Assert.All(rows,row=>Assert.True(row.Valid));
   foreach(var row in rows){Assert.Equal(Encoding.UTF8.GetBytes(records[row.RelativePath]),row.OriginalBytes);Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(row.OriginalBytes)),row.Sha256);}
   var scene=rows.Single(row=>row.Kind==StashEntityKind.Scene);Assert.True(scene.Document!.Value.GetProperty("organized").GetBoolean());Assert.Equal("preserve",scene.Document.Value.GetProperty("custom_fields").GetProperty("unmapped").GetString());
   Assert.Null(scene.NaturalKey);Assert.Equal("/mnt/library/video.mp4",rows.Single(row=>row.Kind==StashEntityKind.File&&row.Document!.Value.GetProperty("type").GetString()=="video").NaturalKey);
   Assert.Equal("Group",rows.Single(row=>row.Kind==StashEntityKind.Group).NaturalKey);Assert.Contains(rows,row=>row.Kind==StashEntityKind.Folder);
   Assert.Contains(rows.SelectMany(row=>row.Issues),issue=>issue.Code=="gallery-chapters");Assert.Contains(rows.SelectMany(row=>row.Issues),issue=>issue.Code=="subgroups");
   var secret=Encoding.UTF8.GetBytes("SentinelPrivateSource12345");foreach(var file in Directory.GetFiles(Stage,"*",SearchOption.AllDirectories))
   {using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);var bytes=new byte[(int)stream.Length];stream.ReadExactly(bytes);Assert.False(bytes.AsSpan().IndexOf(secret)>=0);}
   using var wrong=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=snapshot.StagingDatabase,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
   Assert.Throws<SqliteException>(()=>{wrong.Open();using var command=wrong.CreateCommand();command.CommandText="SELECT count(*) FROM documents";command.ExecuteScalar();});
   Assert.DoesNotContain(Directory.GetFiles(Stage,"*",SearchOption.AllDirectories),file=>file.EndsWith(".keys.json",StringComparison.Ordinal));
  }
  Assert.Empty(Directory.GetDirectories(Stage,".flixypix-stash-*"));foreach(var(path,json)in records)Assert.Equal(json,File.ReadAllText(Path.Combine(Source,path)));
 }
 [Theory][InlineData("{broken")][InlineData("{} {}")]
 public async Task MalformedJsonDoesNotLoseOriginalSourceBytesOrOtherDocuments(string json)
 {
  const string schemaJson="{\"markers\":[{\"seconds\":1}]}";
  Add("tags/bad.json",json);Add("tags/good.json","{\"name\":\"Good\"}");Add("scenes/schema.json",schemaJson);
  using var snapshot=await StashExport.ReadDirectoryAsync(Source,Stage);Assert.Equal(3,snapshot.Count);Assert.Equal(2,snapshot.ErrorDocuments);
  var rows=snapshot.ReadPage(0);var bad=rows.Single(row=>row.RelativePath=="tags/bad.json");Assert.False(bad.Valid);Assert.Null(bad.Document);Assert.Equal(json,Encoding.UTF8.GetString(bad.OriginalBytes));
  var schema=rows.Single(row=>row.RelativePath=="scenes/schema.json");Assert.False(schema.Valid);Assert.NotNull(schema.Document);Assert.Equal(schemaJson,Encoding.UTF8.GetString(schema.OriginalBytes));Assert.Contains(schema.Issues,issue=>issue.Severity==StashIssueSeverity.Error&&issue.Code=="string");
  Assert.True(rows.Single(row=>row.RelativePath=="tags/good.json").Valid);
 }
 [Fact]
 public async Task AliasesStringAndArrayAndPathBasedGalleryReferencesRemainDistinct()
 {
  Add("performers/a.json","{\"name\":\"A\",\"aliases\":[\"One\",\"Two\"]}");Add("performers/b.json","{\"name\":\"A\",\"disambiguation\":\"Other\",\"aliases\":\"One,Two\"}");
  Add("galleries/a.json","{\"folder_path\":\"/a\",\"title\":\"Same\"}");Add("galleries/b.json","{\"zip_files\":[\"/b.zip\"],\"title\":\"Same\"}");
  using var snapshot=await StashExport.ReadDirectoryAsync(Source,Stage);Assert.Equal(0,snapshot.ErrorDocuments);Assert.Equal(4,snapshot.ReadPage(0).Select(row=>row.NaturalKey).Distinct().Count());
 }
 [Fact]
 public async Task PagingIsStableAndReturnsOnlyTheRequestedBoundedWindow()
 {
  for(var i=29;i>=0;i--)Add($"tags/{i:D2}.json",JsonSerializer.Serialize(new{name="Tag"+i}));
  using var snapshot=await StashExport.ReadDirectoryAsync(Source,Stage);Assert.Equal(30,snapshot.Count);var first=snapshot.ReadPage(0,12);var second=snapshot.ReadPage(12,12);Assert.Equal("tags/00.json",first[0].RelativePath);Assert.Equal("tags/12.json",second[0].RelativePath);Assert.Equal(6,snapshot.ReadPage(24,12).Count);Assert.Empty(snapshot.ReadPage(30));
  Assert.Throws<ArgumentOutOfRangeException>(()=>snapshot.ReadPage(0,257));snapshot.Dispose();Assert.Throws<ObjectDisposedException>(()=>snapshot.ReadPage(0));
 }
 [Theory][InlineData("entity")][InlineData("total")][InlineData("files")][InlineData("cancel")]
 public async Task LimitsOrCancellationCleanOwnedStagingWithoutChangingSource(string kind)
 {
  var file=Add("tags/a.json",JsonSerializer.Serialize(new{name="A",details=new string('s',kind=="entity"?2000:600)}));Add("tags/b.json",JsonSerializer.Serialize(new{name="B",details=new string('s',600)}));var before=File.ReadAllBytes(file);using var stop=new CancellationTokenSource();
  var limits=kind switch{"files"=>new StashReadLimits(MaximumFiles:1),"entity"=>new StashReadLimits(MaximumEntityBytes:1024),"total"=>new StashReadLimits(MaximumEntityBytes:1024,MaximumTotalBytes:1024),_=>new StashReadLimits()};
  if(kind=="cancel")await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>StashExport.ReadDirectoryAsync(Source,Stage,limits,stop.Token,new Immediate<StashReadProgress>(_=>stop.Cancel())));
  else await Assert.ThrowsAsync<InvalidDataException>(()=>StashExport.ReadDirectoryAsync(Source,Stage,limits));
  Assert.Equal(before,File.ReadAllBytes(file));Assert.Empty(Directory.GetDirectories(Stage,".flixypix-stash-*"));
 }
 [Fact]
 public async Task MissingExportsAndNestedStagingRejectBeforeAnyCatalogChanges()
 {
  await Assert.ThrowsAsync<ArgumentException>(()=>StashExport.ReadDirectoryAsync(Source,Path.Combine(Source,"stage")));Assert.False(Directory.Exists(Path.Combine(Source,"stage")));
  await Assert.ThrowsAsync<InvalidDataException>(()=>StashExport.ReadDirectoryAsync(Source,Stage));Assert.Empty(Directory.GetDirectories(Stage,".flixypix-stash-*"));
 }
 [Fact]
 public async Task RootAndSectionJunctionsAreNeverFollowed()
 {
  Add("tags/a.json","{\"name\":\"Keep\"}");var alias=Path.Combine(root,"alias");
  async Task Link(string path,string target)
  {
   var start=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in new[]{"/d","/c","mklink","/J",path,target})start.ArgumentList.Add(arg);
   using var process=Process.Start(start)!;await process.WaitForExitAsync();Assert.Equal(0,process.ExitCode);links.Add(path);
  }
  await Link(alias,Source);await Assert.ThrowsAsync<InvalidDataException>(()=>StashExport.ReadDirectoryAsync(alias,Stage));
  await Link(Path.Combine(Source,"scenes"),Path.Combine(Source,"tags"));await Assert.ThrowsAsync<InvalidDataException>(()=>StashExport.ReadDirectoryAsync(Source,Stage));Assert.Empty(Directory.GetDirectories(Stage,".flixypix-stash-*"));
 }
 private sealed class Immediate<T>(Action<T> action):IProgress<T>{public void Report(T value)=>action(value);}
 [Theory][InlineData("same-size")][InlineData("added")][InlineData("removed")][InlineData("renamed")]
 public async Task RevalidationRejectsChangedSourceBeforeLaterApply(string kind)
 {
  var path=Add("tags/a.json","{\"name\":\"Original\"}");using var snapshot=await StashExport.ReadDirectoryAsync(Source,Stage);await snapshot.VerifySourceAsync();
  if(kind=="same-size")File.WriteAllText(path,"{\"name\":\"Modified\"}");
  if(kind=="added")Add("tags/new.json","{\"name\":\"New\"}");
  if(kind=="removed")File.Delete(path);
  if(kind=="renamed")File.Move(path,Path.Combine(Path.GetDirectoryName(path)!,"other.json"));
  await Assert.ThrowsAsync<IOException>(()=>snapshot.VerifySourceAsync());Assert.Equal("Original",Assert.Single(snapshot.ReadPage(0)).Document!.Value.GetProperty("name").GetString());
 }
 [Fact]
 public async Task RepeatedRevalidationSkipsOnlySettledUnchangedFilesAndStillSeesLaterWrites()
 {
  var old=Add("tags/old.json","{\"name\":\"Settled\"}");File.SetLastWriteTimeUtc(old,new DateTime(2001,2,3,4,5,6,DateTimeKind.Utc));
  var fresh=Add("tags/fresh.json","{\"name\":\"Fresh\"}");
  using var snapshot=await StashExport.ReadDirectoryAsync(Source,Stage);await snapshot.VerifySourceAsync();await snapshot.VerifySourceAsync();
  // A settled file that is rewritten with the same length gets a new time stamp and is hashed again.
  File.WriteAllText(old,"{\"name\":\"Changed\"}");
  await Assert.ThrowsAsync<IOException>(()=>snapshot.VerifySourceAsync());
  File.WriteAllText(old,"{\"name\":\"Settled\"}");File.SetLastWriteTimeUtc(old,new DateTime(2001,2,3,4,5,6,DateTimeKind.Utc));await snapshot.VerifySourceAsync();
  // A file written just before its hash is never trusted by its time stamp, even when the stamp is unchanged.
  var stamp=File.GetLastWriteTimeUtc(fresh);File.WriteAllText(fresh,"{\"name\":\"Frash\"}");File.SetLastWriteTimeUtc(fresh,stamp);
  await Assert.ThrowsAsync<IOException>(()=>snapshot.VerifySourceAsync());
 }
 [Fact]
 public async Task InvalidUtf8AndNestedSectionDirectoriesNeverBecomeValidSourceRecords()
 {
  var path=Add("tags/a.json","{}");File.WriteAllBytes(path,[255,254,0]);
  using(var snapshot=await StashExport.ReadDirectoryAsync(Source,Stage)){Assert.Equal(1,snapshot.ErrorDocuments);Assert.Equal(new byte[]{255,254,0},Assert.Single(snapshot.ReadPage(0)).OriginalBytes);}
  Directory.CreateDirectory(Path.Combine(Source,"tags","nested"));await Assert.ThrowsAsync<InvalidDataException>(()=>StashExport.ReadDirectoryAsync(Source,Stage));Assert.Empty(Directory.GetDirectories(Stage,".flixypix-stash-*"));
 }
 [Fact]
 public async Task ErrorListsStayBoundedAndStillPreventInvalidDocumentAcceptance()
 {
  var duplicates=string.Join(',',Enumerable.Repeat("\"name\":\"A\"",1000));Add("tags/a.json","{"+duplicates+"}");using var snapshot=await StashExport.ReadDirectoryAsync(Source,Stage);var document=Assert.Single(snapshot.ReadPage(0));Assert.False(document.Valid);Assert.Equal(256,document.Issues.Count);
 }
 [Fact]
 public async Task CancellationWhileRevalidatingLeavesSnapshotAvailableAndSourceUntouched()
 {
  var path=Add("tags/a.json","{\"name\":\"Keep\"}");using var snapshot=await StashExport.ReadDirectoryAsync(Source,Stage);using var stop=new CancellationTokenSource();stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>snapshot.VerifySourceAsync(stop.Token));Assert.True(Assert.Single(snapshot.ReadPage(0)).Valid);Assert.Equal("{\"name\":\"Keep\"}",File.ReadAllText(path));
 }
}
