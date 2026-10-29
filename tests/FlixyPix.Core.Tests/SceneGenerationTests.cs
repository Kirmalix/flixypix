using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class SceneGenerationTests : IDisposable
{
 private readonly string root = Path.Combine(Path.GetTempPath(),"flixypix-generation-" + Guid.NewGuid().ToString("N"));
 private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aHucAAAAASUVORK5CYII=");
 public SceneGenerationTests() => Directory.CreateDirectory(root);

 [Fact]
 public void GenerationIsIdempotentAndReplacesOnlyExplicitGeneratedAssets()
 {
  using var catalog = new CatalogRepository(Path.Combine(root,"catalog.db"));
  var media = catalog.Upsert(new() { Path = Path.Combine(root,"video.mp4"), Kind = MediaKind.Video });
  var bookmark = catalog.UpsertBookmark(new() { MediaId = media.Id, Title = "Manual", PositionSeconds = 1.2 });
  var custom = catalog.UpsertMediaAsset(new() { MediaId = media.Id, Kind = MediaAssetKind.ScenePreview, Data = Png, IsCustom = true });
  var id = Guid.NewGuid(); var first = catalog.ApplyGeneratedScenePreviews(media.Id,id,[new(0.5,Png),new(1.5,Png)]);
  var repeated = catalog.ApplyGeneratedScenePreviews(media.Id,id,[new(0.5,Png),new(1.5,Png)]);
  Assert.Equal(first.Select(a => a.Id),repeated.Select(a => a.Id)); Assert.Equal(3,catalog.GetMediaAssets(media.Id).Count);
  var replacement = catalog.ApplyGeneratedScenePreviews(media.Id,Guid.NewGuid(),[new(0.75,Png)],[first[0].Id]);
  var assets = catalog.GetMediaAssets(media.Id);
  Assert.Equal(3,assets.Count); Assert.DoesNotContain(assets,a => a.Id == first[0].Id);
  Assert.Contains(assets,a => a.Id == first[1].Id); Assert.Contains(assets,a => a.Id == custom.Id);
  Assert.Equivalent(bookmark,Assert.Single(catalog.GetBookmarks(media.Id)),strict:true);
  Assert.Throws<ArgumentException>(() => catalog.ApplyGeneratedScenePreviews(media.Id,Guid.NewGuid(),[new(0,Png)],[custom.Id]));
  Assert.Equal(3,catalog.GetMediaAssets(media.Id).Count);
 }

 [Fact]
 public void InvalidGenerationDoesNotPartiallyReplaceExistingAssetsAndEditingRetainsSceneReferences()
 {
  using var catalog = new CatalogRepository(Path.Combine(root,"catalog.db"));
  var media = catalog.Upsert(new() { Path = Path.Combine(root,"video.mp4"), Kind = MediaKind.Video });
  var first = Assert.Single(catalog.ApplyGeneratedScenePreviews(media.Id,Guid.NewGuid(),[new(0.5,Png)]));
  Assert.Throws<ArgumentException>(() => catalog.ApplyGeneratedScenePreviews(media.Id,Guid.NewGuid(),[new(1,Png),new(double.NaN,Png)],[first.Id]));
  Assert.Equal(first.Id,Assert.Single(catalog.GetMediaAssets(media.Id)).Id);
  var scene = catalog.UpsertScene(new() { MediaId = media.Id, Title = "Before", StartSeconds = 0.1, EndSeconds = 1 });
  catalog.UpsertPlaylist(new() { Name = "References", MediaIds = [media.Id], Entries = [new(media.Id,scene.Id)] });
  catalog.UpsertScene(scene with { Title = "After", StartSeconds = 0.2, EndSeconds = 0.8, Description = "Edited" });
  Assert.Equal(scene.Id,Assert.Single(catalog.GetPlaylists()).Entries[0].SceneId);
  Assert.Equal("After",Assert.Single(catalog.GetScenes(media.Id)).Title);
 }

 public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root,true); }
}
