using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;
public sealed class AbLoopTests
{
 [Fact] public void StartAndEndAreSeparateFromSceneRangesAndNewStartClearsEarlierEnd()
 {
  var state=new AbLoopState();Assert.False(state.Active);
  state=state.WithStart(0,10);Assert.False(state.Active);state=state.WithEnd(10,10);Assert.True(state.Active);
  var changed=state.WithStart(4,10);Assert.Equal(10,changed.End);Assert.Equal(0,state.Start);
  state=state.WithStart(10,10);Assert.Null(state.End);Assert.False(state.Active);
  Assert.Equal(new AbLoopState(),new AbLoopState().Validate(10));
 }
 [Theory][InlineData(-1,2)][InlineData(2,2)][InlineData(3,2)][InlineData(1,11)]
 public void InvalidEndNeverChangesValidState(double start,double end)
 {
  var valid=new AbLoopState{Start=1,End=3};Assert.Throws<ArgumentException>(()=>new AbLoopState{Start=start,End=end}.Validate(10));Assert.True(valid.Active);
 }
 [Fact] public void MissingStartUnknownNumbersAndInvalidDurationAreRejected()
 {
  Assert.Throws<ArgumentException>(()=>new AbLoopState().WithEnd(2,10));
  foreach(var invalid in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity,-1d})
  {
   Assert.Throws<ArgumentException>(()=>new AbLoopState().WithStart(invalid,10));
   Assert.Throws<ArgumentException>(()=>new AbLoopState{Start=0,End=invalid}.Validate(10));
  }
  Assert.Throws<ArgumentException>(()=>new AbLoopState().Validate(0));
 }
}
