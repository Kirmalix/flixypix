using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlixyPix.Core;
using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;
public partial class MainWindow
{
 private async Task RunFramePreviewSmokeAsync(VideoPlayerWindow window,string root,CatalogRepository db,MediaItem media)
 {
  static void Require(bool value,string text){if(!value)throw new InvalidOperationException(text);}
  var oldFiles=Directory.GetFiles(root,"*",SearchOption.AllDirectories).Order().ToArray();
  window.ConfigureSeekPreview(_tools);await window.LoadSeekFrameAsync(.5,CancellationToken.None);
  Require(window.PreviewPicture is {PixelWidth:480,PixelHeight:270,IsFrozen:true}&&window.PreviewCacheUsage.Entries==1,"Native memory frame did not reach the preview.");
  var first=window.PreviewPicture;await window.LoadSeekFrameAsync(.5,CancellationToken.None);Require(window.PreviewCacheUsage.Entries==1,"Repeat frame request added another cache entry.");
  await window.LoadSeekFrameAsync(1,CancellationToken.None);Require(window.PreviewCacheUsage.Entries==2,"Second position did not get independent cache identity.");
  Require(oldFiles.SequenceEqual(Directory.GetFiles(root,"*",SearchOption.AllDirectories).Order()),"Seek preview wrote plaintext frame files.");
  // Explicit synthetic acceptance artifact, after the no-frame-file check; production never writes this image.
  var previewArtifact=new PngBitmapEncoder();previewArtifact.Frames.Add(BitmapFrame.Create(window.PreviewPicture!));
  using(var output=File.Create(Path.Combine(root,"memory-preview.png")))previewArtifact.Save(output);
  var configured=window.FrameAt;var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
  window.FrameAt=async(_,_)=>
  {
   var call=++calls;if(call==1){started.TrySetResult();await release.Task;}
   return new(2,2,Enumerable.Repeat((byte)call,12).ToArray());
  };
  var stale=window.LoadSeekFrameAsync(2,CancellationToken.None);await started.Task;
  var latest=window.LoadSeekFrameAsync(3,CancellationToken.None);await latest;var wanted=window.PreviewPicture;release.TrySetResult();await stale;
  Require(wanted!=null&&ReferenceEquals(wanted,window.PreviewPicture),"Cancelled late frame replaced newer image.");
  window.FrameAt=configured;window.ClearSeekPreview();await window.LoadSeekFrameAsync(.5,CancellationToken.None);
  var privacy=_settings.Privacy;
  var beforeLock=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var afterLock=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  window.FrameAt=async(_,_)=>{beforeLock.TrySetResult();await afterLock.Task;return new(2,2,new byte[12]);};
  var lateLockFrame=window.LoadSeekFrameAsync(1,CancellationToken.None);await beforeLock.Task;
  try
  {
   AppShell.Configure(_settings with {Privacy=privacy with {PinHash=PinCode.Hash("2468")}});
   Require(AppLock.Lock(),"Preview lock fixture could not lock.");Require(window.PreviewPicture==null&&window.PreviewCacheUsage==(0,0L),"Privacy lock retained decrypted preview buffers.");
   afterLock.TrySetResult();await lateLockFrame;Require(window.PreviewPicture==null,"Late decoder callback displayed a frame after locking.");window.FrameAt=configured;
   await window.LoadSeekFrameAsync(1,CancellationToken.None);Require(window.PreviewPicture==null,"Locked player accepted another frame.");
   await AppLock.Prompt!.EnterAsync("2468");Require(!AppLock.IsLocked,"Preview lock fixture did not unlock.");
  }
  finally{afterLock.TrySetResult();window.FrameAt=configured;AppShell.Configure(_settings);}
  await window.LoadSeekFrameAsync(.5,CancellationToken.None);Require(window.PreviewCacheUsage.Entries==1,"Cache could not restart after unlock.");
  await window.PauseForPrivacyAsync();Require(window.PreviewPicture==null&&window.PreviewCacheUsage==(0,0L),"Privacy hide retained preview buffers.");
  await window.LoadSeekFrameAsync(.5,CancellationToken.None);
  var changed=window.Source!;var modified=File.GetLastWriteTimeUtc(changed.Path);File.SetLastWriteTimeUtc(changed.Path,modified.AddSeconds(2));
  try{await window.LoadSeekFrameAsync(.5,CancellationToken.None);Require(window.PreviewPicture==null&&window.PreviewCacheUsage==(0,0L),"Changed source displayed an old cached frame.");}
  finally{File.SetLastWriteTimeUtc(changed.Path,modified);}
  var closing=new VideoPlayerWindow(this,db.GetMedia(media.Id)!,db,PlaybackSource.Resolve(db.GetMedia(media.Id)!,media.Files.Single().Path));closing.Show();
  started=new(TaskCreationOptions.RunContinuationsAsynchronously);release=new(TaskCreationOptions.RunContinuationsAsynchronously);
  closing.FrameAt=async(_,_)=>{started.TrySetResult();await release.Task;return new(2,2,new byte[12]);};
  var pending=closing.LoadSeekFrameAsync(1,CancellationToken.None);await started.Task;closing.Close();release.TrySetResult();await pending;
  Require(closing.PreviewPicture==null&&closing.PreviewCacheUsage==(0,0L),"Closed player retained a late preview.");
  File.WriteAllText(Path.Combine(root,"frame-preview-result.txt"),"PASS: actual in-memory decoder, repeat/time cache, no frame files, newest UI request wins, actual PIN lock/restart, privacy hide/source invalidation and late close.");
 }
}
