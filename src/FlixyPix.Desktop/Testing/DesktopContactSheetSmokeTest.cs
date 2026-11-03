using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlixyPix.Core;
using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private async Task RunContactSheetSeekingSmokeAsync(CatalogRepository db,Guid mediaId,string file,string root)
 {
  static void Require(bool check,string error){if(!check)throw new InvalidOperationException(error);}
  var result=await _tools.CreateContactSheetResultAsync(file,Path.Combine(root,"seek-sheet.jpg"),12,160d/90,new(){Columns=2,Rows=2,Width=640,Border=8,FastSeek=false,Header=["Contact sheet · exact version"],FontFile=FontFiles.FindOrDefault("Segoe UI")});
  var original=db.GetMedia(mediaId)!;var sheet=result.Metadata();
  db.Upsert(original with {ThumbnailPath=result.ImagePath,ContactSheet=sheet});
  var other=db.GetMedia(mediaId)!.Files.Single().Path;
  db.SetMainVersion(mediaId,other); // The sheet still belongs to the previous main file.
  var calls=0;string? playedPath=null;double? position=null;
  async Task PlaySheet(MediaItem item,double seconds,string path)
  {
   calls++;playedPath=path;Require(path==file&&item.Path!=path,"Contact sheet followed the preferred/main version instead of its original source.");
   var window=new VideoPlayerWindow(this,item,db,PlaybackSource.Resolve(item,path));window.Show();MpvPlayer? player=null;
   try
   {
    player=await _tools.PlayAsync(path,seconds,await window.ReadyTask);await player.PauseAsync(true);await window.AttachPlayerAsync(player);
    position=await player.GetPositionAsync();Require(position.HasValue&&Math.Abs(position.Value-seconds)<.6&&window.Source?.Path==file,"Contact sheet did not seek the native decoder to its stored frame time/source.");
   }
   finally{window.Close();if(player!=null)await player.DisposeAsync();}
  }
  async Task<ContactSheetWindow> Open()
  {
   var window=new ContactSheetWindow(this,db,mediaId,PlaySheet);window.Show();await window.ReadyTask;
   await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);window.UpdateLayout();return window;
  }
  Point ViewPoint(ContactSheetWindow window,double x,double y)
  {
   var width=window.Viewport.ActualWidth;var height=window.Viewport.ActualHeight;var scale=Math.Min(width/sheet.Width,height/sheet.Height);
   return new((width-sheet.Width*scale)/2+x*scale,(height-sheet.Height*scale)/2+y*scale);
  }
  var preview=await Open();
  try
  {
   Require(preview.CanSeek,"Generated sheet was not seekable after main-version change: "+preview.StatusText);
   foreach(var size in new[]{new Size(760,500),new Size(1100,800)})
   {
    preview.Width=size.Width;preview.Height=size.Height;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);preview.UpdateLayout();
    foreach(var point in new[]{new Point(0,0),ViewPoint(preview,sheet.Width/2d,1),ViewPoint(preview,sheet.Cells[0].Bounds.X+sheet.Cells[0].Bounds.Width+4,sheet.Cells[0].Bounds.Y+10)})await preview.SelectAtAsync(point.X,point.Y);
    Require(calls==0&&preview.IsLoaded,"Header, outer border, letterbox or gap started playback.");
   }
   Capture((FrameworkElement)preview.Content,Path.Combine(root,"contact-sheet.png"));
   var cell=sheet.Cells[^1];var click=ViewPoint(preview,cell.Bounds.X+cell.Bounds.Width/2d,cell.Bounds.Y+cell.Bounds.Height/2d);
   await preview.SelectAtAsync(click.X,click.Y);Require(calls==1&&playedPath==file&&Math.Abs(position!.Value-cell.Seconds)<.6,"Valid tile did not play the exact version/time.");
  }
  finally{preview.Close();}
  preview=await Open();
  try{preview.FrameChoice.SelectedIndex=0;preview.PlayButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await preview.SelectionTask;Require(calls==2,"Keyboard-accessible frame selector did not start playback.");}
  finally{preview.Close();}
  var current=db.GetMedia(mediaId)!;
  db.Upsert(current with {ContactSheet=null});preview=await Open();
  try{Require(!preview.CanSeek,"Old sheet without metadata was seekable.");await preview.SelectAtAsync(100,100);Require(calls==2,"Old sheet started playback.");}
  finally{preview.Close();db.Upsert(current);}
  preview=await Open();
  try
  {
   db.Upsert(db.GetMedia(mediaId)! with {ThumbnailPath=null});var point=ViewPoint(preview,sheet.Cells[0].Bounds.X+10,sheet.Cells[0].Bounds.Y+10);await preview.SelectAtAsync(point.X,point.Y);
   Require(!preview.CanSeek&&calls==2,"A replaced thumbnail was allowed to seek.");
  }
  finally{preview.Close();db.Upsert(current);}
  var stamp=File.GetLastWriteTimeUtc(file);File.SetLastWriteTimeUtc(file,stamp.AddSeconds(2));preview=await Open();
  try{Require(!preview.CanSeek,"Changed source was seekable.");}
  finally{preview.Close();File.SetLastWriteTimeUtc(file,stamp);}
  preview=await Open();var missing=file+".contact-sheet-away";
  try
  {
   File.Move(file,missing);var point=ViewPoint(preview,sheet.Cells[0].Bounds.X+10,sheet.Cells[0].Bounds.Y+10);await preview.SelectAtAsync(point.X,point.Y);
   Require(!preview.CanSeek&&calls==2,"Source disappearing during an open preview was allowed to seek.");
  }
  finally{preview.Close();if(File.Exists(missing))File.Move(missing,file);}
  var bytes=File.ReadAllBytes(result.ImagePath);File.Copy(result.ImagePath,Path.Combine(root,"original-contact-sheet.jpg"));
  var foreign=await _tools.CreateThumbnailAsync(file,Path.Combine(root,"custom-contact.jpg"),0);
  File.Copy(foreign,result.ImagePath,true);preview=await Open();
  try{Require(!preview.CanSeek,"Different image bytes inherited old cell metadata.");}
  finally{preview.Close();File.WriteAllBytes(result.ImagePath,bytes);}
  File.WriteAllBytes(result.ImagePath,[137,80,78,71,13,10,26,10,0]);preview=await Open();
  try{Require(!preview.CanSeek&&!string.IsNullOrWhiteSpace(preview.StatusText),"Corrupt image did not produce a retained, nonseekable error state.");}
  finally{preview.Close();File.WriteAllBytes(result.ImagePath,bytes);}
  db.SetMainVersion(mediaId,file);
  await RunVersionBackupPlaybackSmokeAsync(db,mediaId,file,other,root);
  File.WriteAllText(Path.Combine(root,"contact-sheet-result.txt"),"PASS: actual generated image/hash, main-version change retains exact source, native mpv time, scaled/header/border/gap hit mapping, frame selector, old sheet, thumbnail replacement, changed/disappearing source, different image bytes and corrupt image error.");
 }

 private async Task RunVersionBackupPlaybackSmokeAsync(CatalogRepository db,Guid mediaId,string sheetSource,string main,string root)
 {
  static void Require(bool check,string error){if(!check)throw new InvalidOperationException(error);}
  db.SetMainVersion(mediaId,main);
  try
  {
   var package=Path.Combine(root,"versions.backup");await db.CreateBackupAsync(package,true);
   var secret=db.IsEncrypted?"synthetic-player-preferences-password":null;
   Require((await CatalogRepository.VerifyBackupAsync(package,secret:secret)).MediaFiles==2,"Backup verification did not include both actual video versions.");
   var restored=await CatalogRepository.RestoreBackupAsync(package,Path.Combine(root,"versions-restored"),secret:secret);
   using var key=db.IsEncrypted?CatalogEncryption.Unlock(restored.DatabasePath,secret!):null;using var destination=new CatalogRepository(restored.DatabasePath,key);
   var item=destination.GetMedia(mediaId)!;var sheet=item.ContactSheet!;var opened=false;
   Require(restored.RestoredMediaFiles==2&&File.Exists(item.Path)&&File.Exists(item.Files.Single().Path),"Both version files were not restored.");
   Require(sheet.SourcePath==item.Files.Single().Path&&sheet.SourcePath!=sheetSource&&sheet.MatchesSource(item)&&sheet.MatchesImage(item.ThumbnailPath!),"Restored sheet lost its additional-version path/hash/stamp binding.");
   var preview=new ContactSheetWindow(this,destination,mediaId,async(current,seconds,path)=>
   {
    Require(path==sheet.SourcePath&&path!=current.Path,"Restored contact sheet opened the main version instead of the saved additional source.");
    var playerWindow=new VideoPlayerWindow(this,current,destination,PlaybackSource.Resolve(current,path));playerWindow.Show();MpvPlayer? player=null;
    try
    {
     player=await _tools.PlayAsync(path,seconds,await playerWindow.ReadyTask);await player.PauseAsync(true);await playerWindow.AttachPlayerAsync(player);
     Require(Math.Abs((await player.GetPositionAsync())!.Value-seconds)<.6,"Restored additional version did not seek to its stored frame.");opened=true;
    }
    finally{playerWindow.Close();if(player!=null)await player.DisposeAsync();}
   });
   preview.Show();
   try
   {
    await preview.ReadyTask;Require(preview.CanSeek,"Restored additional-version contact sheet is not seekable.");
    preview.FrameChoice.SelectedIndex=1;preview.PlayButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await preview.SelectionTask;Require(opened,"Restored contact sheet did not open actual native playback.");
   }
   finally{preview.Close();}
   File.WriteAllText(Path.Combine(root,"version-backup-result.txt"),"PASS: full backup/verify/restore includes both actual files, contact sheet bound to restored additional version, native decoder seek and preserved source stamps, normal/encrypted fixture.");
  }
  finally{db.SetMainVersion(mediaId,sheetSource);}
 }
}
