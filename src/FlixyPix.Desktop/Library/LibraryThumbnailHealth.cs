using System.Diagnostics;
using System.IO;
using FlixyPix.Core;
namespace FlixyPix.Desktop;
public partial class MainWindow
{
 private CancellationTokenSource? _thumbnailCheck;
 private bool _thumbnailsChecked;
 /// <summary>The check running in the background, for whoever must wait for its result.</summary>
 internal Task _thumbnailCheckRun=Task.CompletedTask;

 /// <summary>The thumbnail filter answers from the catalog at once. Switching it on also starts one pass over videos and audio
 /// that finds thumbnails that are entered but missing or damaged; images need none. Refreshing the list does not restart it.</summary>
 private void UpdateThumbnailCheck(ViewState state)
 {
  if(state.Filter.HasThumbnail is null) {_thumbnailCheck?.Cancel();_thumbnailsChecked=false;return;}
  if(_thumbnailsChecked)return;
  _thumbnailsChecked=true;
  _thumbnailCheckRun=_libraryTasks.Run(_ => CheckThumbnailsAsync());
 }

 private async Task CheckThumbnailsAsync()
 {
  using var check=CancellationTokenSource.CreateLinkedTokenSource(_libraryTasks.Token);_thumbnailCheck=check;
  var token=check.Token;var repository=_repository;
  var sinceRefresh=Stopwatch.StartNew();var listChanged=false;var shown=T("Checking thumbnails…");
  void Show(string text) {if(_operation==null)Status.Text=shown=text;}
  void UpdateList()
  {
   listChanged=false;sinceRefresh.Restart();
   // With "with thumbnail" a medium leaves because of this check, not because of an edit.
   _ignoreLeavingOnce=_filter.HasThumbnail==true;Refresh();
  }
  Show(shown);
  try
  {
   var total=await Task.Run(()=>CheckedKinds.Sum(kind=>repository.QueryPage(new MediaQuery {Kind=kind,Limit=1},token).TotalCount),token);
   long done=0;
   foreach(var kind in CheckedKinds)
    for(var offset=0;;offset+=CheckPageSize)
    {
     var start=offset;
     var (count,changed)=await Task.Run(()=>CheckThumbnailPage(repository,kind,start,token),token);
     listChanged|=changed;done+=count;
     if(_closing)return;
     Show(T("Checking thumbnails: {0:N0} / {1:N0}",Math.Min(done,total),total));
     if(count<CheckPageSize)break;
     if(listChanged&&sinceRefresh.Elapsed>TimeSpan.FromSeconds(5))UpdateList();
    }
   if(listChanged)UpdateList();
  }
  catch(Exception error) when(error is OperationCanceledException or ObjectDisposedException) { }
  catch(Exception error) when(error is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) {ErrorLog.Write("Thumbnail check",error);}
  finally
  {
   if(_thumbnailCheck==check)_thumbnailCheck=null;
   if(Status.Text==shown)Status.Text="";
  }
 }

 private const int CheckPageSize=200;
 private static readonly MediaKind[] CheckedKinds=[MediaKind.Video,MediaKind.Audio];

 /// <returns>How many media were read, and whether a result differs from what the filter assumed so far.</returns>
 private (int Count,bool Changed) CheckThumbnailPage(CatalogRepository repository,MediaKind kind,int offset,CancellationToken token)
 {
  var page=repository.QueryPage(new MediaQuery {Kind=kind,Offset=offset,Limit=CheckPageSize},token);
  var results=new List<ThumbnailHealthResult>();var changed=false;
  foreach(var item in page.Items)
  {
   token.ThrowIfCancellationRequested();
   if(_closing)break;
   var path=string.IsNullOrEmpty(item.ThumbnailPath)?null:item.ThumbnailPath;
   var cached=repository.GetThumbnailHealth(item.Id);
   // Without a thumbnail the catalog alone gives the answer.
   if(path==null&&cached==null)continue;
   var signature=path??"";var available=false;
   try
   {
    var file=path==null?null:new FileInfo(path);
    if(file?.Exists==true)
    {
     signature+="|"+file.Length+"|"+file.LastWriteTimeUtc.Ticks;
     available=cached?.Signature==signature?cached.Value.Available:LooksReadable(path!);
    }
   }
   catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException) { }
   if(cached?.Signature!=signature||cached.Value.Available!=available)results.Add(new(item.Id,path,signature,available));
   if(available!=(cached?.Available??path!=null))changed=true;
  }
  repository.SaveCheckedThumbnailHealth(results);
  return (page.Items.Count,changed);
 }

 /// <summary>The first bytes tell a common picture from a broken file without decoding it, which is what keeps the pass quick
 /// over many thousands of media. Only anything else gets decoded. A common format cut off further on still counts as readable.</summary>
 internal static bool LooksReadable(string path)
 {
  var head=new byte[12];
  int read;
  using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,1)) read=stream.Read(head);
  if(read==0)return false;
  var start=head.AsSpan(0,read);
  if(start.StartsWith<byte>([0xFF,0xD8,0xFF])||start.StartsWith<byte>([0x89,0x50,0x4E,0x47])||start.StartsWith("GIF8"u8)||start.StartsWith("BM"u8))return true;
  if(read>=12&&start.StartsWith("RIFF"u8)&&start[8..12].SequenceEqual("WEBP"u8))return true;
  return CanDecode(path);
 }

 /// <summary>Tiles read formats Windows does not know through the image service, so a picture counts when either can read it.</summary>
 private static bool CanDecode(string path)
 {
  if(MediaCard.LoadBitmap(path,32)!=null)return true;
  try {return DesktopImaging.Service.DecodeAsync(path,maxWidth:32,maxHeight:32).GetAwaiter().GetResult().PngBytes.Length>0;}
  catch(Exception error) when(error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or NotSupportedException) {return false;}
 }
}
