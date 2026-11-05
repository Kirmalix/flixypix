using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class BackgroundUpdateTests : IDisposable
{
 private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-background-" + Guid.NewGuid().ToString("N"));
 private CatalogRepository Open() { Directory.CreateDirectory(root); return new(Path.Combine(root,"catalog.db")); }
 private MediaItem Item(CatalogRepository catalog)
 {
  var path = Path.Combine(root,"video.mp4"); File.WriteAllText(path,"synthetic");
  return catalog.Upsert(new() { Path=path,Title="Before",Kind=MediaKind.Video });
 }
 private static TechnicalAnalysis Probe(MediaItem item)
 {
  var expected = MediaVersions.Main(item); var file = new FileInfo(item.Path);
  return new(expected,expected with { Width=1920, Height=1080 },file.Length,file.LastWriteTimeUtc);
 }
 [Fact]
 public void AnalysisAndRestorationPreserveEditsMadeAfterTheirSnapshot()
 {
  using var catalog=Open();var item=Item(catalog);var result=Probe(item);
  catalog.Upsert(item with {Title="Edited",Rating=9,Tags=["Keep"]});
  Assert.True(catalog.ApplyTechnicalAnalysis(item.Id,[result]));
  Assert.True(catalog.RestoreThumbnail(item,"restored.jpg"));
  var saved=catalog.GetMedia(item.Id)!;
  Assert.Equal("Edited",saved.Title);Assert.Equal(9,saved.Rating);Assert.Equal(["Keep"],saved.Tags);Assert.Equal(1920,saved.Width);
 }
 [Fact]
 public void AnalysisRejectsChangedSourceAndDeletedMedium()
 {
  using var catalog=Open();var item=Item(catalog);var result=Probe(item);
  File.AppendAllText(item.Path,"changed");
  Assert.False(catalog.ApplyTechnicalAnalysis(item.Id,[result]));
  Assert.Null(catalog.GetMedia(item.Id)!.Width);
  catalog.Delete(item.Id);
  Assert.False(catalog.ApplyTechnicalAnalysis(item.Id,[result]));
  Assert.False(catalog.RestoreThumbnail(item,"restored.jpg"));
 }
 [Fact]
 public void AnalysisDoesNotApplyOldMainToReplacementOrOverwriteNewTechnicalEdits()
 {
  using var catalog=Open();var item=Item(catalog);var result=Probe(item);
  catalog.Upsert(item with {Width=1280});
  Assert.True(catalog.ApplyTechnicalAnalysis(item.Id,[result]));
  Assert.Equal(1280,catalog.GetMedia(item.Id)!.Width);
  catalog.Upsert(catalog.GetMedia(item.Id)! with {Path=Path.Combine(root,"other.mp4"),Width=null,Height=null});
  Assert.False(catalog.ApplyTechnicalAnalysis(item.Id,[result]));
 }
 [Fact]
 public void ThumbnailChangesRejectOldRestorationAndHealth()
 {
  using var catalog=Open();var item=Item(catalog);
  catalog.Upsert(item with {ThumbnailPath="chosen.jpg"});
  Assert.False(catalog.RestoreThumbnail(item,"restored.jpg"));
  catalog.SaveCheckedThumbnailHealth(new ThumbnailHealthResult[] {new(item.Id,null,"",false)});
  Assert.Null(catalog.GetThumbnailHealth(item.Id));
  catalog.SaveCheckedThumbnailHealth(new ThumbnailHealthResult[] {new(item.Id,"chosen.jpg","chosen",true)});
  Assert.True(catalog.GetThumbnailHealth(item.Id)!.Value.Available);
 }
 public void Dispose() { if(Directory.Exists(root))Directory.Delete(root,true); }
}
