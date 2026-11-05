using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Desktop.Tests;

public sealed class WorklistStateTests
{
 [Fact]
 public void OldRequestCannotKeepMembersInNewView()
 {
  var state = new WorklistState(); var view = new ViewState {Filter=new() {UnratedOnly=true}};
  var id = Guid.NewGuid(); state.Commit(state.Begin(view,[],1),new HashSet<Guid>());state.Shown=view;
  var slow = state.Begin(view,[id],2);
  var fast = state.Begin(view with {SearchText="other"},[],3);
  Assert.True(state.Commit(fast,new HashSet<Guid>()));
  Assert.False(state.Commit(slow,new HashSet<Guid>()));Assert.Empty(state.Kept);
 }
 [Fact]
 public void LeavingMembersStayUntilExplicitUpdateAndSurviveSort()
 {
  var state=new WorklistState();var view=new ViewState {Filter=new() {UnratedOnly=true}};var id=Guid.NewGuid();
  state.Commit(state.Begin(view,[],1),new HashSet<Guid>());state.Shown=view;
  state.Commit(state.Begin(view,[id],2),new HashSet<Guid>());Assert.Contains(id,state.Kept);
  state.Commit(state.Begin(view with {SortOrder=1},[],3),new HashSet<Guid>());Assert.Contains(id,state.Kept);
  state.DropNext=true;state.Commit(state.Begin(view,[],4),new HashSet<Guid>());Assert.Empty(state.Kept);
 }
}
