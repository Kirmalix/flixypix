using FlixyPix.Core;
using Xunit;

namespace FlixyPix.MediaTools.Tests;
public sealed class VideoFrameCacheTests:IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-frame-cache-"+Guid.NewGuid().ToString("N"));
 public VideoFrameCacheTests()=>Directory.CreateDirectory(root);
 public void Dispose()=>Directory.Delete(root,true);
 private PlaybackSource Source(string name="one.mp4")
 {var path=Path.Combine(root,name);File.WriteAllBytes(path,[1]);return PlaybackSource.Resolve(new(){Path=path,Kind=MediaKind.Video,DurationSeconds=10000},path);}
 private static MemoryVideoFrame Frame(int width,int height,byte value=0)=>new(width,height,Enumerable.Repeat(value,width*height*3).ToArray());
 [Fact] public async Task CacheKeysVersionStampTimeAndSizeAndEvictsLeastRecentlyUsed()
 {
  var calls=0;using var cache=new VideoFrameCache((_,_,w,h,_)=>{calls++;return Task.FromResult(Frame(w,h));});var source=Source();
  await cache.GetAsync(source,0,2,2);await cache.GetAsync(source,.01,2,2);Assert.Equal(1,calls);
  await cache.GetAsync(source,0,3,2);Assert.Equal(2,calls);
  var other=Source("version.mp4") with {MediaId=source.MediaId};await cache.GetAsync(other,0,2,2);Assert.Equal(3,calls);
  cache.Clear();for(var i=0;i<128;i++)await cache.GetAsync(source,i,2,2);Assert.Equal(128,cache.Usage.Entries);
  await cache.GetAsync(source,0,2,2);var before=calls;await cache.GetAsync(source,128,2,2);await cache.GetAsync(source,0,2,2);Assert.Equal(before+1,calls);
  await cache.GetAsync(source,1,2,2);Assert.Equal(before+2,calls);Assert.Equal(128,cache.Usage.Entries);
  File.SetLastWriteTimeUtc(source.Path,source.ModifiedAtUtc.UtcDateTime.AddSeconds(2));await Assert.ThrowsAsync<IOException>(()=>cache.GetAsync(source,0,2,2));Assert.Equal((0,0L),cache.Usage);
  var fresh=source with {ModifiedAtUtc=new FileInfo(source.Path).LastWriteTimeUtc};await cache.GetAsync(fresh,0,2,2);Assert.Equal(before+3,calls);
 }
 [Fact] public async Task ByteBudgetAndClearAreIndependentOfEntryBudget()
 {
  using var cache=new VideoFrameCache((_,_,w,h,_)=>Task.FromResult(Frame(w,h)));var source=Source();
  for(var i=0;i<30;i++){await cache.GetAsync(source,i,1024,1024);Assert.InRange(cache.Usage.Bytes,0,VideoFrameCache.MaximumBytes);}
  Assert.Equal(21,cache.Usage.Entries);cache.Clear();Assert.Equal((0,0L),cache.Usage);
 }
 [Fact] public async Task EndOfVideoBucketsStayBeforeDurationAndUnknownDurationDoesNotShiftRequest()
 {
  double decoded=-1;using var cache=new VideoFrameCache((_,time,w,h,_)=>{decoded=time;return Task.FromResult(Frame(w,h));});var source=Source() with {DurationSeconds=2};
  await cache.GetAsync(source,2,2,2);Assert.Equal(1.9,decoded);
  await cache.GetAsync(source with {DurationSeconds=null},.5,2,2);Assert.Equal(.5,decoded);
 }
 [Fact] public async Task NewestRequestWinsEvenIfDecoderIgnoresCancellationAndOnlyOneDecoderRuns()
 {
  var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;var active=0;var maximum=0;
  using var cache=new VideoFrameCache(async(_,_,w,h,_)=>
  {
   maximum=Math.Max(maximum,Interlocked.Increment(ref active));var call=Interlocked.Increment(ref calls);
   try{if(call==1){started.SetResult();await release.Task;}return Frame(w,h,(byte)call);}finally{Interlocked.Decrement(ref active);}
  });
  var source=Source();var first=cache.GetAsync(source,1,2,2);await started.Task;
  var middle=cache.GetAsync(source,2,2,2);var latest=cache.GetAsync(source,3,2,2);release.SetResult();
  await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>middle);
  Assert.Equal(2,(await latest).Pixels.Span[0]);Assert.Equal(1,maximum);Assert.Equal(2,calls);Assert.Equal(1,cache.Usage.Entries);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public async Task ClearOrDisposePreventsLateCacheRefill(bool dispose)
 {
  var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  using var cache=new VideoFrameCache(async(_,_,w,h,_)=>{started.SetResult();await release.Task;return Frame(w,h);});
  var request=cache.GetAsync(Source(),1,2,2);await started.Task;if(dispose)cache.Dispose();else cache.Clear();release.SetResult();
  await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>request);Assert.Equal((0,0L),cache.Usage);
  if(dispose)await Assert.ThrowsAsync<ObjectDisposedException>(()=>cache.GetAsync(Source("two.mp4"),1,2,2));
 }
 [Fact] public async Task SourceChangeDuringDecodeAndMalformedFramesAreNotCached()
 {
  var source=Source();using(var cache=new VideoFrameCache((_,_,w,h,_)=>{File.WriteAllBytes(source.Path,[1,2]);return Task.FromResult(Frame(w,h));}))
  {await Assert.ThrowsAsync<IOException>(()=>cache.GetAsync(source,1,2,2));Assert.Equal((0,0L),cache.Usage);}
  source=source with {Size=2,ModifiedAtUtc=new FileInfo(source.Path).LastWriteTimeUtc};
  using var bad=new VideoFrameCache((_,_,w,h,_)=>Task.FromResult(new MemoryVideoFrame(w,h,new byte[1])));await Assert.ThrowsAsync<InvalidDataException>(()=>bad.GetAsync(source,1,2,2));Assert.Equal((0,0L),bad.Usage);
 }
}
