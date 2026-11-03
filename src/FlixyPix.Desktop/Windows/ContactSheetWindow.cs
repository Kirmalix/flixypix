using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

internal sealed class ContactSheetWindow:Window
{
 private readonly CatalogRepository repository;
 private readonly Guid mediaId;
 private readonly Func<MediaItem,double,string,Task> play;
 private readonly CancellationTokenSource lifetime=new();
 private readonly Grid viewport=new(){Background=Brushes.Transparent};
 private readonly Image image=new(){Stretch=Stretch.Uniform};
 private readonly ComboBox frames=new(){MinWidth=150,Margin=new Thickness(0,8,8,0)};
 private readonly Button start=new(){Content=T("Play"),Margin=new Thickness(0,8,0,0),IsEnabled=false};
 private readonly TextBlock source=new(){TextWrapping=TextWrapping.Wrap,Foreground=SettingsUi.Muted,Margin=new Thickness(0,0,0,8)};
 private readonly WindowFrame frame;
 private MediaItem? loaded;
 private ContactSheetMetadata? metadata;
 private bool seeking;
 internal Task ReadyTask{get;}
 internal bool CanSeek=>metadata!=null&&!seeking&&!lifetime.IsCancellationRequested&&!AppLock.IsLocked;
 internal Grid Viewport=>viewport;
 internal ComboBox FrameChoice=>frames;
 internal Button PlayButton=>start;
 internal string StatusText=>frame.Status.Text;
 internal Task SelectionTask{get;private set;}=Task.CompletedTask;

 internal ContactSheetWindow(Window owner,CatalogRepository repository,Guid mediaId,Func<MediaItem,double,string,Task> play)
 {
  Owner=owner;this.repository=repository;this.mediaId=mediaId;this.play=play;
  Title=T("View contact sheet");Width=1000;Height=740;MinWidth=480;MinHeight=360;WindowStartupLocation=WindowStartupLocation.CenterOwner;
  var body=new DockPanel();DockPanel.SetDock(source,Dock.Top);body.Children.Add(source);
  var actions=new WrapPanel();actions.Children.Add(frames);actions.Children.Add(start);DockPanel.SetDock(actions,Dock.Bottom);body.Children.Add(actions);
  viewport.Children.Add(image);body.Children.Add(viewport);
  frame=new WindowFrame(Title,T("Click a frame to play this file from the displayed time."),body,scroll:false);frame.Add(T("Close"),Close,cancel:true);Content=frame;
  frame.Status.TextWrapping=TextWrapping.Wrap;frame.Status.TextTrimming=TextTrimming.None;
  viewport.MouseLeftButtonUp+=(_,e)=>{var point=e.GetPosition(viewport);_=SelectAtAsync(point.X,point.Y);};
  viewport.MouseMove+=(_,e)=>{var p=e.GetPosition(viewport);viewport.Cursor=CanSeek&&metadata!.HitTestView(p.X,p.Y,viewport.ActualWidth,viewport.ActualHeight)!=null?Cursors.Hand:Cursors.Arrow;};
  start.Click+=(_,_)=>{if(frames.SelectedIndex>=0&&metadata is {} sheet)SelectionTask=SelectCellAsync(sheet.Cells[frames.SelectedIndex]);};
  Closed+=(_,_)=>{lifetime.Cancel();image.Source=null;metadata=null;};
  ReadyTask=LoadAsync();
 }
 private async Task LoadAsync()
 {
  try
  {
   loaded=repository.GetMedia(mediaId);if(loaded?.ThumbnailPath is not {} path)throw new IOException(T("File missing"));
   var decoded=await DesktopImaging.Service.DecodeAsync(path,maxWidth:1600,maxHeight:1600,cancellationToken:lifetime.Token);
   if(lifetime.IsCancellationRequested)return;
   image.Source=DesktopImaging.Bitmap(decoded.PngBytes);
   var sheet=loaded.ContactSheet;
   if(sheet!=null&&sheet.Width==decoded.OriginalWidth&&sheet.Height==decoded.OriginalHeight&&
    sheet.ImageSha256.Equals(decoded.SourceSha256,StringComparison.OrdinalIgnoreCase)&&sheet.MatchesSource(loaded))
   {
    sheet.Validate();metadata=sheet;source.Text=sheet.SourcePath;
    frames.ItemsSource=sheet.Cells.Select(cell=>JumpMarks.FormatTime(cell.Seconds)+" · "+cell.Seconds.ToString("0.000",System.Globalization.CultureInfo.CurrentCulture)+" s").ToArray();
    frames.SelectedIndex=0;start.IsEnabled=true;
   }
   else{source.Text=path;frame.Status.Text=T("This contact sheet cannot be used for seeking. Recreate the thumbnail to enable it.");}
  }
  catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
  catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or ImageMagick.MagickException or NotSupportedException)
  {if(!lifetime.IsCancellationRequested)frame.Status.Text=error.Message;}
 }
 internal Task SelectAtAsync(double x,double y)=>CanSeek&&metadata!.HitTestView(x,y,viewport.ActualWidth,viewport.ActualHeight)is {} cell?SelectCellAsync(cell):Task.CompletedTask;
 private async Task SelectCellAsync(ContactSheetCell cell)
 {
  if(!CanSeek||loaded?.ThumbnailPath is not {} imagePath||metadata is not {} sheet)return;
  seeking=true;start.IsEnabled=false;
  try
  {
   bool MatchesCurrent(MediaItem? current)=>current?.ContactSheet is {} latest&&current.ThumbnailPath==imagePath&&latest.ImageSha256==sheet.ImageSha256&&
    latest.Width==sheet.Width&&latest.Height==sheet.Height&&latest.SourcePath==sheet.SourcePath&&latest.SourceSize==sheet.SourceSize&&
    latest.SourceModifiedAtUtc==sheet.SourceModifiedAtUtc&&latest.Cells.SequenceEqual(sheet.Cells)&&sheet.MatchesSource(current);
   var current=repository.GetMedia(mediaId);var valid=MatchesCurrent(current);
   valid=valid&&await Task.Run(()=>sheet.MatchesImage(imagePath),lifetime.Token);
   if(lifetime.IsCancellationRequested||AppLock.IsLocked)return;
   current=repository.GetMedia(mediaId);valid=valid&&MatchesCurrent(current);
   if(!valid||current==null){metadata=null;frame.Status.Text=T("The contact sheet or its source changed. Reopen it or recreate the thumbnail.");return;}
   Close();await play(current,cell.Seconds,sheet.SourcePath);
  }
  catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
  catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
  {if(!lifetime.IsCancellationRequested)frame.Status.Text=error.Message;}
  finally{seeking=false;start.IsEnabled=CanSeek;}
 }
}

public partial class MainWindow
{
 internal ContactSheetWindow ShowContactSheet(MediaItem item)
 {
  var window=new ContactSheetWindow(this,_repository,item.Id,(current,seconds,file)=>Play(current,seconds,file));window.Show();return window;
 }
}
