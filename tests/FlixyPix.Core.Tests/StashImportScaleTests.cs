using System.Diagnostics;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace FlixyPix.Core.Tests;

/// <summary>A large Stash export through the same paged preview and 256-record batches the import wizard uses.</summary>
public sealed class StashImportScaleTests(ITestOutputHelper output):IDisposable
{
 private const int Scenes=10_000,PerformerCount=500,TagCount=200,Batch=256;
 private readonly string root=StashTestWorkspace.Create("flixypix-stash-scale-");
 public void Dispose(){SqliteConnection.ClearAllPools();if(Environment.GetEnvironmentVariable("FLIXYPIX_TEST_CAPTURE")!="1")Directory.Delete(root,true);}

 [Fact,Trait("Category","Slow")]
 public async Task TenThousandScenesPreviewAndImportInBatchesWithinBudgets()
 {
  var source=Path.Combine(root,"source");var media=Path.Combine(root,"media");Directory.CreateDirectory(media);
  void Add(string relative,string json){var path=Path.Combine(source,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,json);}
  for(var i=0;i<PerformerCount;i++)Add($"performers/p{i}.json",$$"""{"name":"Performer {{i}}"}""");
  for(var i=0;i<TagCount;i++)Add($"tags/t{i}.json",$$"""{"name":"Tag {{i}}"}""");
  for(var i=0;i<Scenes;i++)
  {
   File.WriteAllText(Path.Combine(media,$"v{i}.mp4"),"v"+i);
   Add($"scenes/s{i}.json",$$"""{"title":"Scene {{i}}","files":["/library/v{{i}}.mp4"],"tags":["Tag {{i%TagCount}}"],"performers":["Performer {{i%PerformerCount}}"],"markers":[{"title":"M","seconds":"1.5"}]}""");
  }
  using var db=new CatalogRepository(Path.Combine(root,"catalog.db"));
  var profile=db.SaveStashSource(new(){Name="Scale",SourceDirectory=source,PathMappings=[new("/library",media)]});
  var peak=0L;void Sample(){peak=Math.Max(peak,GC.GetTotalMemory(false));}
  var clock=Stopwatch.StartNew();
  using var snapshot=await StashExport.ReadDirectoryAsync(source,Path.Combine(root,"stage"));
  var read=clock.Elapsed;Sample();
  async Task<List<StashImportPreviewRow>> PreviewAll()
  {
   var rows=new List<StashImportPreviewRow>();
   for(var offset=0;offset<snapshot.Count;){var page=await Task.Run(()=>db.PreviewStashImport(snapshot,profile.Id,"Performers","Studios",offset,Batch));if(page.Count==0)break;offset+=page.Count;rows.AddRange(page);Sample();}
   return rows;
  }
  clock.Restart();var first=await PreviewAll();var preview=clock.Elapsed;
  var chosen=first.Where(r=>r.Supported&&r.Draft.Kind is StashEntityKind.Performer or StashEntityKind.Tag or StashEntityKind.Scene)
   .OrderBy(r=>r.Draft.Kind==StashEntityKind.Scene?1:0).ToArray();
  Assert.Equal(Scenes+PerformerCount+TagCount,chosen.Length);
  clock.Restart();var created=0;var slowestBatch=TimeSpan.Zero;
  for(var start=0;start<chosen.Length;start+=Batch)
  {
   var batch=Stopwatch.StartNew();
   var result=await db.ApplyStashImportAsync(snapshot,new(profile.Id,profile.Revision,"Performers","Studios",chosen.Skip(start).Take(Batch).Select(r=>new StashImportItem(r.Draft.SourceOrdinal)).ToArray()));
   created+=result.Created;profile=db.GetStashSource(profile.Id)!;Sample();if(batch.Elapsed>slowestBatch)slowestBatch=batch.Elapsed;
  }
  var apply=clock.Elapsed;
  Assert.Equal(chosen.Length,created);Assert.Equal(Scenes,db.Search().Count);
  clock.Restart();var repeat=await PreviewAll();var again=clock.Elapsed;
  Assert.All(repeat.Where(r=>r.Draft.Kind==StashEntityKind.Scene),r=>Assert.Equal(StashIdentityState.Matched,r.Identity.State));
  output.WriteLine($"documents {snapshot.Count:N0}: read {read.TotalSeconds:F1}s, preview {preview.TotalSeconds:F1}s, import {apply.TotalSeconds:F1}s (slowest batch {slowestBatch.TotalSeconds:F2}s), repeat preview {again.TotalSeconds:F1}s, managed peak {peak/1048576}MiB");
  // About three times the measured 2026-11-03 values (preview 3s, repeat 15s, import 65s, batch 3s); the earlier per-record
  // directory scans (repeat 47s) and full rehash per batch (import 184s) fail them.
  Assert.True(preview<TimeSpan.FromSeconds(30)&&again<TimeSpan.FromSeconds(45),$"preview {preview}, repeat {again}");
  Assert.True(slowestBatch<TimeSpan.FromSeconds(10)&&apply<TimeSpan.FromSeconds(170),$"import {apply}, slowest batch {slowestBatch}");
  Assert.True(peak<1536L*1048576,$"managed peak {peak/1048576}MiB");
 }
}
