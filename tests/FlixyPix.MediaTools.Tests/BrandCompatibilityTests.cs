using System.Security.Cryptography;
using System.Text.Json;
using FlixyPix.MediaTools;
using Xunit;

namespace FlixyPix.MediaTools.Tests;
public sealed class BrandCompatibilityTests:IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-media-brand-"+Guid.NewGuid());
 public BrandCompatibilityTests()=>Directory.CreateDirectory(root);public void Dispose()=>Directory.Delete(root,true);
 [Fact]public void PreviousQueueIsReadWithoutExecutingAndSavedWithNewMarker()
 {
  var id=Guid.NewGuid();var
   path=Path.Combine(root,"jobs.json");File.WriteAllText(path,JsonSerializer.Serialize(new{Format=ProductIdentity.PreviousFormatPrefix+"-media-queue",Version=1,Jobs=new[]{new{Id=id,Input=@"U:\offline\movie.mp4",Output=Path.Combine(root,"never-created.mp4"),Kind="Convert",State="Running"}}}));
  using(var queue=new MediaJobQueue(path,new MediaToolService(new MediaToolPaths()))){var job=Assert.Single(queue.Jobs);Assert.Equal(id,job.Id);Assert.Equal(MediaJobState.Pending,job.State);Assert.Equal(@"U:\offline\movie.mp4",job.Input);Assert.False(File.Exists(job.Output));}
  using var json=JsonDocument.Parse(File.ReadAllText(path));Assert.Equal("flixypix-media-queue",json.RootElement.GetProperty("Format").GetString());
 }
 [Fact]public void PreviousSceneManifestRetainsJobFrameAndSourceIdentities()
 {
  var bytes=new byte[]{137,80,78,71,13,10,26,10};var path=Path.Combine(root,"frame-0000.png");File.WriteAllBytes(path,bytes);var request=new
   ScenePreviewRequest(Guid.NewGuid(),0,1,Count:1);var frame=new
   ScenePreviewFrame(0,0,Path.GetFileName(path),Convert.ToHexStringLower(SHA256.HashData(bytes)));var manifest=new
   ScenePreviewManifest(ProductIdentity.PreviousFormatPrefix+"-scene-previews",1,request,new
   string('a',64),[frame]);File.WriteAllText(Path.Combine(root,"scene-previews.json"),JsonSerializer.Serialize(manifest));var
   result=MediaToolService.ReadScenePreviewManifest(root);Assert.Equal(request,result.Request);Assert.Equal(frame,Assert.Single(result.Frames));Assert.Equal(manifest.SourceSha256,result.SourceSha256);
 }
}
