using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;
internal sealed partial class VideoPlayerWindow
{
 private VideoFrameCache? previewCache;
 private CancellationTokenSource? seekPreviewWork;
 private long previewGeneration;
 internal (int Entries,long Bytes) PreviewCacheUsage=>previewCache?.Usage??(0,0);
 internal BitmapSource? PreviewPicture=>seekImage.Source as BitmapSource;
 internal void ConfigureSeekPreview(MediaToolService tools)
 {
  ClearSeekPreview();previewCache?.Dispose();previewCache=new(tools);
  FrameAt=async(seconds,token)=>Source is {} source?await previewCache.GetAsync(source,seconds,480,270,token):null;
 }
 internal async Task LoadSeekFrameAsync(double seconds,CancellationToken token)
 {
  if(closing||AppLock.IsLocked)return;
  HideSeekPreview();var ownGeneration=previewGeneration;using var work=CancellationTokenSource.CreateLinkedTokenSource(token);seekPreviewWork=work;
  try
  {
   await Task.Delay(120,work.Token);
   if(FrameAt==null||await FrameAt(seconds,work.Token) is not {} frame)return;
   work.Token.ThrowIfCancellationRequested();
   if(closing||AppLock.IsLocked||ownGeneration!=previewGeneration)return;
   var picture=BitmapSource.Create(frame.Width,frame.Height,96,96,PixelFormats.Rgb24,null,frame.Pixels.ToArray(),frame.Stride);picture.Freeze();
   seekImage.Source=picture;seekPreview.IsOpen=true;seekPreview.VerticalOffset=-(240*frame.Height/(double)frame.Width+52);
  }
  catch(Exception error)when(error is OperationCanceledException or System.IO.IOException or InvalidOperationException or ArgumentException){}
  finally{if(ReferenceEquals(seekPreviewWork,work))seekPreviewWork=null;}
 }
 private void HideSeekPreview()
 {
  previewGeneration++;seekPreviewWork?.Cancel();seekPreviewWork=null;seekPreview.IsOpen=false;seekImage.Source=null;
 }
 internal void ClearSeekPreview(){HideSeekPreview();previewCache?.Clear();}
 private void DisposeSeekPreview(){ClearSeekPreview();previewCache?.Dispose();previewCache=null;FrameAt=null;}
}
