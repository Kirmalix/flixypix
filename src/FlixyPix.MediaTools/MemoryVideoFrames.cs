using System.Diagnostics;

namespace FlixyPix.MediaTools;

public sealed record MemoryVideoFrame(int Width,int Height,ReadOnlyMemory<byte> Pixels)
{
 public int Stride=>checked(Width*3);
 public long ByteLength=>Pixels.Length;
}
public sealed partial class MediaToolService
{
 /// <summary>One fixed-size RGB24 frame on stdout. No image or frame file is created.</summary>
 public async Task<MemoryVideoFrame> DecodeFrameAsync(string input,double seconds,int width,int height,CancellationToken token=default)
 {
  token.ThrowIfCancellationRequested();
  ValidateTime(seconds,nameof(seconds));
  if(width is <1 or >2048||height is <1 or >2048)throw new ArgumentOutOfRangeException(nameof(width));
  var source=Input(input);
  var frame=await Decode(false);
  if(frame.Length==0&&seconds>0)frame=await Decode(true);
  if(frame.Length!=checked(width*height*3))throw new InvalidDataException("The decoder did not return a complete preview frame.");
  return new(width,height,frame);
  async Task<byte[]> Decode(bool accurate)
  {
   var seek=new[]{"-ss",F(seconds)};
   var info=StartInfo(Resolve(paths.Ffmpeg,"ffmpeg"),["-hide_banner","-loglevel","error","-nostdin",..accurate?Array.Empty<string>():seek,"-i",source,..accurate?seek:Array.Empty<string>(),"-map","0:v:0","-frames:v","1","-vf",$"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1","-f","rawvideo","-pix_fmt","rgb24","pipe:1"]);
   info.RedirectStandardOutput=true;info.RedirectStandardError=true;
   using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(PreviewTimeout);
   using var process=new Process{StartInfo=info};process.Start();
   using var kill=timeout.Token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}});
   var output=ReadPixels();var errors=ReadBoundedAsync(process.StandardError,128*1024,timeout.Token);
   try
   {
    await process.WaitForExitAsync(timeout.Token);var bytes=await output;var error=await errors;token.ThrowIfCancellationRequested();timeout.Token.ThrowIfCancellationRequested();
    if(process.ExitCode!=0)throw new InvalidOperationException("Preview decoder failed: "+error[..Math.Min(error.Length,1800)]);
    return bytes;
   }
   catch(Exception)when(timeout.IsCancellationRequested){token.ThrowIfCancellationRequested();throw new MediaToolTimeoutException("The preview decoder timed out.");}
   finally
   {
    if(!process.HasExited)process.Kill(true);
    await process.WaitForExitAsync();
    try{await Task.WhenAll(output,errors);}catch(Exception error)when(error is OperationCanceledException or IOException or InvalidDataException){}
   }
   async Task<byte[]> ReadPixels()
   {
    // One extra byte detects malformed/excess output without an unbounded buffer.
    var buffer=new byte[checked(width*height*3+1)];var count=0;
    while(count<buffer.Length)
    {
     var read=await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(count),timeout.Token);if(read==0)break;count+=read;
    }
    if(count==buffer.Length){process.Kill(true);throw new InvalidDataException("Preview decoder exceeded the frame size.");}
    return count==buffer.Length-1?buffer.AsSpan(0,count).ToArray():count==0?[]:throw new InvalidDataException("Incomplete preview frame.");
   }
  }
 }
}
