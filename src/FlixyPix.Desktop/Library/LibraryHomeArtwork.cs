using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using FlixyPix.Core;
namespace FlixyPix.Desktop;
public partial class MainWindow
{
 private readonly SemaphoreSlim _homeArtworkGate=new(1);
 private CancellationTokenSource _homeArtworkCancellation=new();
 private async Task<ImageSource?> LoadFeaturedVideoArtworkAsync(MediaItem item,CancellationToken requestToken)
 {
  if(item.Kind!=MediaKind.Video)return null;
  using var linked=CancellationTokenSource.CreateLinkedTokenSource(requestToken,_homeArtworkCancellation.Token);
  var token=linked.Token;const int width=1920;
  try
  {
   await _homeArtworkGate.WaitAsync(token);
   try
   {
    if(_closing||_view!="home")return null;
    string? source=null;
    if(item.Extra.ContainsKey("thumbnail.assetId"))source=item.ThumbnailPath;
    if(source==null)
    {
     var asset=await Task.Run(()=>_repository.GetMediaAssets(item.Id).Where(a=>a.Kind==MediaAssetKind.Artwork).OrderByDescending(a=>a.IsCustom).ThenBy(a=>a.Position).FirstOrDefault(),token);
     if(asset!=null)source=await Task.Run(()=>_repository.MaterializeMediaAsset(asset,Path.Combine(_settings.DataDirectory,"image-cache","home")),token);
    }
    if(source!=null) {
     var original=await DesktopImaging.LoadBitmapAsync(source,width,token);
     await Task.Run(()=>TrimHomeCache(source),token);return original;
    }
    if(item.Kind!=MediaKind.Video||!MediaPresence.Exists(item.Path))return null;
    var file=new FileInfo(item.Path);
    var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Path+"|"+file.Length+"|"+file.LastWriteTimeUtc.Ticks+"|home-v1")));
    var directory=Path.Combine(_settings.DataDirectory,"image-cache","home");Directory.CreateDirectory(directory);
    var output=Path.Combine(directory,key+".jpg");
    if(!File.Exists(output))
    {
     var temporary=Path.Combine(directory,key+".tmp.jpg");
     try { await _tools.CreateArtworkAsync(item.Path,temporary,Math.Min(item.DurationSeconds.GetValueOrDefault()/10,30),token);File.Move(temporary,output,true); }
     finally { if(File.Exists(temporary))File.Delete(temporary); }
    }
    File.SetLastWriteTimeUtc(output,DateTime.UtcNow);
    var bitmap=await DesktopImaging.LoadBitmapAsync(output,width,token);
    await Task.Run(()=>TrimHomeCache(output),token);
    return bitmap;
   }
   finally {_homeArtworkGate.Release();}
  }
  catch(Exception error) when(error is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException or ArgumentException) {return null;}
 }
 private void TrimHomeCache(string keep)
 {
  var directory=Path.Combine(_settings.DataDirectory,"image-cache","home");if(!Directory.Exists(directory))return;
  long total=0;
  foreach(var file in new DirectoryInfo(directory).GetFiles().OrderByDescending(f=>f.LastWriteTimeUtc))
  {total+=file.Length;if(total>1024L*1024*1024&&!string.Equals(file.FullName,keep,StringComparison.OrdinalIgnoreCase))file.Delete();}
 }

}
