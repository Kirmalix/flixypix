using System.IO;
using FlixyPix.Core;
namespace FlixyPix.Desktop;
public partial class MainWindow
{
 private async Task CheckThumbnailsAsync(long version)
 {
  Status.Text=T("Checking thumbnails…");
  var candidates=CaptureViewState() with {Filter=_filter with {HasThumbnail=null}};
  var query=candidates.ToQuery(DateTimeOffset.Now) with {Offset=0,Limit=200};int done=0;
  while(version==_refreshVersion&&!_closing)
  {
   var page=await Task.Run(()=>_repository.QueryPage(query));
   foreach(var item in page.Items)
   {
    if(version!=_refreshVersion||_closing)return;
    await Task.Run(()=> {
     var path=item.ThumbnailPath??(item.Kind==MediaKind.Image?item.Path:null);string signature=path??"";bool available=false;var cached=_repository.GetThumbnailHealth(item.Id);
     try { var file=path==null?null:new FileInfo(path);if(file?.Exists==true) {signature+="|"+file.Length+"|"+file.LastWriteTimeUtc.Ticks;available=cached?.Signature==signature?cached.Value.Available:MediaCard.LoadBitmap(path,32)!=null;} }
     catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException) { }
     if(cached?.Signature!=signature||cached.Value.Available!=available)_repository.SaveThumbnailHealth(item.Id,signature,available);
    });done++;
   }
   if(version!=_refreshVersion||_closing)return;
   Status.Text=T("Checking thumbnails: {0:N0} / {1:N0}",done,page.TotalCount);
   if(page.Items.Count<query.Limit)break;query=query with {Offset=query.Offset+query.Limit};
  }
  if(version==_refreshVersion&&!_closing)Status.Text="";
 }
}
