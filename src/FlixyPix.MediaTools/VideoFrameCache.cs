using FlixyPix.Core;

namespace FlixyPix.MediaTools;

/// <summary>Per-player, bounded memory cache. A newer request cancels any older decode.</summary>
public sealed class VideoFrameCache:IDisposable
{
 public const int MaximumEntries=128;
 public const long MaximumBytes=64L*1024*1024;
 private sealed record Key(Guid Media,string Path,long Size,long Modified,long Time,int Width,int Height);
 private sealed record Entry(Key Key,MemoryVideoFrame Frame);
 private readonly object gate=new();
 private readonly SemaphoreSlim decoderGate=new(1,1);
 private readonly Func<string,double,int,int,CancellationToken,Task<MemoryVideoFrame>> decode;
 private readonly Dictionary<Key,LinkedListNode<Entry>> entries=[];
 private readonly LinkedList<Entry> lru=new();
 private CancellationTokenSource? active;
 private long bytes,generation;
 private bool disposed;
 public VideoFrameCache(MediaToolService tools):this(tools.DecodeFrameAsync){}
 public VideoFrameCache(Func<string,double,int,int,CancellationToken,Task<MemoryVideoFrame>> decode)=>this.decode=decode??throw new ArgumentNullException(nameof(decode));
 public (int Entries,long Bytes) Usage{get{lock(gate)return(entries.Count,bytes);}}
 public void Clear(){lock(gate){generation++;active?.Cancel();entries.Clear();lru.Clear();bytes=0;}}
 public void Dispose(){lock(gate){if(disposed)return;disposed=true;Clear();}}
 public async Task<MemoryVideoFrame> GetAsync(PlaybackSource source,double seconds,int width,int height,CancellationToken token=default)
 {
  ArgumentNullException.ThrowIfNull(source);
  if(!double.IsFinite(seconds)||seconds<0||seconds>long.MaxValue/10d||source.DurationSeconds is {} duration&&(!double.IsFinite(duration)||duration<=0)||width is <1 or >2048||height is <1 or >2048)throw new ArgumentOutOfRangeException(nameof(seconds));
  token.ThrowIfCancellationRequested();
  // Hundred-millisecond buckets cap churn without collapsing long videos into coarse percentages.
  var bounded=source.DurationSeconds is {} knownDuration?Math.Min(seconds,Math.Max(0,knownDuration-.05)):seconds;
  var time=(long)Math.Floor(bounded*10);
  var key=new Key(source.MediaId,Path.GetFullPath(source.Path).ToUpperInvariant(),source.Size,source.ModifiedAtUtc.UtcTicks,time,width,height);
  CancellationTokenSource work;long ownGeneration;
  lock(gate)
  {
   ObjectDisposedException.ThrowIf(disposed,this);active?.Cancel();ownGeneration=++generation;
   EnsureSource();
   if(entries.TryGetValue(key,out var hit)){lru.Remove(hit);lru.AddFirst(hit);return hit.Value.Frame;}
   work=CancellationTokenSource.CreateLinkedTokenSource(token);active=work;
  }
  try
  {
   await decoderGate.WaitAsync(work.Token);
   try
   {
    work.Token.ThrowIfCancellationRequested();EnsureSource();
    var frame=await decode(source.Path,time/10d,width,height,work.Token).ConfigureAwait(false);
    work.Token.ThrowIfCancellationRequested();EnsureSource();
    if(frame.Width!=width||frame.Height!=height||frame.ByteLength!=checked(width*height*3))throw new InvalidDataException("Invalid cached preview frame.");
    lock(gate)
    {
     work.Token.ThrowIfCancellationRequested();if(disposed||ownGeneration!=generation)throw new OperationCanceledException(work.Token);
     while(entries.Count>=MaximumEntries||bytes+frame.ByteLength>MaximumBytes)
     {var last=lru.Last!;bytes-=last.Value.Frame.ByteLength;entries.Remove(last.Value.Key);lru.RemoveLast();}
     entries.Add(key,lru.AddFirst(new Entry(key,frame)));bytes+=frame.ByteLength;return frame;
    }
   }
   finally{decoderGate.Release();}
  }
  finally{lock(gate){if(ReferenceEquals(active,work))active=null;work.Dispose();}}
  void EnsureSource()
  {
   var file=new FileInfo(source.Path);
   if(!file.Exists||file.Length!=source.Size||file.LastWriteTimeUtc.Ticks!=source.ModifiedAtUtc.UtcTicks){Clear();throw new IOException("The preview source changed or is unavailable.");}
  }
 }
}
