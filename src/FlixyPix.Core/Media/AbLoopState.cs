namespace FlixyPix.Core;

/// <summary>Session-only loop points, independent of saved scenes and playlist repeat.</summary>
public sealed record AbLoopState
{
 public double? Start {get;init;}
 public double? End {get;init;}
 public bool Active=>Start.HasValue&&End.HasValue;
 public AbLoopState Validate(double? duration=null)
 {
  if(duration is {} length&&(!double.IsFinite(length)||length<=0)||
   Start is {} a&&(!double.IsFinite(a)||a<0||duration is {} maxA&&a>maxA)||
   End is {} b&&(!double.IsFinite(b)||Start is not {} start||b<=start||duration is {} maxB&&b>maxB))
   throw new ArgumentException(T("Choose an A–B range within the video, with B after A."));
  return this;
 }
 public AbLoopState WithStart(double seconds,double duration)=>new AbLoopState{Start=seconds,End=End>seconds?End:null}.Validate(duration);
 public AbLoopState WithEnd(double seconds,double duration)=>(this with {End=seconds}).Validate(duration);
}
