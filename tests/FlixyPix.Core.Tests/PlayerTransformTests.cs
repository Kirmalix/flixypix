using FlixyPix.Core;
using System.Text.Json;
using Xunit;

namespace FlixyPix.Core.Tests;
public sealed class PlayerTransformTests
{
 [Fact]
 public void LegacyGeometryNormalizesToPlayerAreaAndUsesDiscreteLatestStep()
 {
  var first=PlayerTransformTimeline.FromLegacy(10,-200,-100,2000,1000,1000,500);
  var second=PlayerTransformTimeline.FromLegacy(20,0,0,1000,500,1000,500);
  var timeline=PlayerTransformTimeline.Validate(new() { Steps=[second,first] });
  Assert.Equal(PlayerTransformTimeline.Identity,timeline.At(5)); Assert.Equal(first,timeline.At(10)); Assert.Equal(first,timeline.At(19.999)); Assert.Equal(second,timeline.At(20));
  Assert.Equal(-.2,first.X); Assert.Equal(-.2,first.Y); Assert.Equal(2,first.Width); Assert.Equal(2,first.Height);
  Assert.Equal(PlayerTransformTimeline.Identity,(timeline with { Enabled=false }).At(15));
 }
 [Fact]
 public void InvalidGeometryAndDuplicateTimesAreRejected()
 {
  Assert.Throws<ArgumentException>(()=>PlayerTransformTimeline.FromLegacy(0,0,0,10,10,0,10));
  Assert.Throws<ArgumentException>(()=>PlayerTransformTimeline.Validate(new() { Steps=[new(0,0,0,0,1)] }));
  Assert.Throws<ArgumentException>(()=>PlayerTransformTimeline.Validate(new() { Steps=[new(0,double.NaN,0,1,1)] }));
  Assert.Throws<ArgumentException>(()=>PlayerTransformTimeline.Validate(new() { Steps=[new(0,0,0,1,1),new(0,0,0,2,2)] }));
 }
 [Fact]
 public void TimelinePersistsInOpenCatalogWithoutLosingUnknownMetadata()
 {
  var path=Path.Combine(Path.GetTempPath(),"flixypix-transform-"+Guid.NewGuid()+".db");
  try
  {
   Guid id;
   using(var repository=new CatalogRepository(path))
   {
    var item=repository.Upsert(new() { Path=Path.Combine(Path.GetTempPath(),"video.mp4"),Kind=MediaKind.Video,Extra=new() { ["legacy.values"]="unchanged" } }); id=item.Id;
    repository.SavePlayerTransforms(id,new() { Steps=[new(2,-.25,-.25,1.5,1.5)] });
   }
   using(var reopened=new CatalogRepository(path)) { Assert.Equal(1.5,reopened.GetPlayerTransforms(id).At(3).Width); Assert.Equal("unchanged",reopened.GetMedia(id)!.Extra["legacy.values"]); }
  }
  finally { foreach(var suffix in new[]{"","-wal","-shm"})File.Delete(path+suffix); }
 }
 [Fact]
 public void LegacyRowsProjectNormalizedTransformsAndRetainInvalidRows()
 {
  var root=Path.Combine(Path.GetTempPath(),"flixypix-legacy-transform-"+Guid.NewGuid()); Directory.CreateDirectory(root);
  try
  {
   object Schema(string table,params string[] columns)=>new { type="schema",table,columns=columns.Select(name=>new { name }) };
   object Row(string table,object values)=>new { type="row",table,values };
   object[] records=[
    new { type="header",format="flixypix-legacy",version=1,sourceSha256=new string('b',64) },
    Schema("movies","ID","fullpath","filename"),Row("movies",new { ID=1,fullpath=root,filename="video.mp4" }),
    Schema("videotransform","ID","videoID","secs","boundsx","boundsy","boundsw","boundsh","displayw","displayh"),
    Row("videotransform",new { ID=1,videoID=1,secs=10,boundsx=-100,boundsy=-50,boundsw=2000,boundsh=1000,displayw=1000,displayh=500 }),
    Row("videotransform",new { ID=2,videoID=1,secs=20,boundsx=0,boundsy=0,boundsw=1000,boundsh=500,displayw=0,displayh=500 }),
    new { type="footer",tables=2,rows=3,tableRows=new Dictionary<string,int> { ["movies"]=1,["videotransform"]=2 } }
   ];
   var input=Path.Combine(root,"legacy.jsonl"); File.WriteAllLines(input,records.Select(record=>JsonSerializer.Serialize(record)));
   using var repository=new CatalogRepository(Path.Combine(root,"catalog.db")); var report=repository.ImportLegacyJsonl(input); Assert.Empty(report.Errors); Assert.Contains(report.Warnings,w=>w.Contains("Original display dimensions"));
   var media=Assert.Single(repository.Search()); var timeline=repository.GetPlayerTransforms(media.Id);
   Assert.Equal(2,timeline.Steps.Count); Assert.Equal(PlayerTransformTimeline.Identity,timeline.At(9)); Assert.Equal(-.1,timeline.At(10).X); Assert.Equal(2,timeline.At(10).Width);
   var json=Path.Combine(root,"catalog.json"); repository.Export(json);
   using var other=new CatalogRepository(Path.Combine(root,"restored.db")); Assert.Empty(other.ImportJson(json).Errors); Assert.Equal(2,other.GetPlayerTransforms(media.Id).At(15).Width);
  }
  finally { Directory.Delete(root,true); }
 }
}
