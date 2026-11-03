using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class StashFieldConflictTests
{
 private static JsonElement J<T>(T value)=>JsonSerializer.SerializeToElement(value);
 [Theory]
 [InlineData("same","same",null,null,StashFieldDecision.KeepLocal,StashFieldStatus.Equal)]
 [InlineData("source","local",null,null,StashFieldDecision.KeepLocal,StashFieldStatus.New)]
 [InlineData("new","old","old","old",StashFieldDecision.UseSource,StashFieldStatus.SourceChanged)]
 [InlineData("old","manual","old","old",StashFieldDecision.UseSource,StashFieldStatus.SourceUnchanged)]
 [InlineData("old","manual","old","local",StashFieldDecision.KeepLocal,StashFieldStatus.PreviouslyKept)]
 [InlineData("new","manual","old","old",StashFieldDecision.UseSource,StashFieldStatus.Conflict)]
 public void ThreeWayStatusNeverSelectsAnOverwriteByDefault(string source,string local,string? beforeSource,string? beforeLocal,StashFieldDecision decision,StashFieldStatus expected)
 {
  var before=beforeSource==null?Array.Empty<StashFieldBaseline>():[new StashFieldBaseline("title",J(beforeSource),J(beforeLocal),decision)];var difference=Assert.Single(StashFieldConflicts.Compare([new("title",J(source),J(local))],before));Assert.Equal(expected,difference.Status);Assert.Equal(StashFieldDecision.KeepLocal,difference.DefaultDecision);Assert.Equal(local,difference.Local.GetString());
 }
 [Fact]
 public void RejectedSourceRemainsRejectedAndUncheckedOrAbsentFieldsAreRetained()
 {
  StashFieldObservation[] observations=[new("title",J("source"),J("manual")),new("description",J("remote"),J("local"))];var recorded=StashFieldConflicts.Record(observations,[new("title",StashFieldDecision.KeepLocal)]);var title=Assert.Single(recorded);Assert.Equal("manual",title.Local.GetString());Assert.Equal(StashFieldStatus.PreviouslyKept,Assert.Single(StashFieldConflicts.Compare([observations[0]],recorded)).Status);
  var empty=StashFieldConflicts.Record([],[],recorded);Assert.Equal("title",Assert.Single(empty).Field);var changed=Assert.Single(StashFieldConflicts.Compare([new("title",J("changed source"),J("manual"))],recorded));Assert.Equal(StashFieldStatus.SourceChanged,changed.Status);Assert.Equal(StashFieldDecision.KeepLocal,changed.DefaultDecision);
 }
 [Fact]
 public void ExplicitUseSourceRecordsNewAppliedBaselineWithoutWritingTargets()
 {
  var values=StashFieldConflicts.Record([new("rating",J(9),J(3))],[new("rating",StashFieldDecision.UseSource)]);var baseline=Assert.Single(values);Assert.Equal(9,baseline.Local.GetInt32());Assert.Equal(9,baseline.Source.GetInt32());Assert.Equal(StashFieldDecision.UseSource,baseline.Decision);var later=Assert.Single(StashFieldConflicts.Compare([new("rating",J(10),J(7))],values));Assert.Equal(StashFieldStatus.Conflict,later.Status);
 }
 [Fact]
 public void StructuredComparisonIgnoresObjectPropertyOrderButPreservesArrayOrderAndNulls()
 {
  using var source=JsonDocument.Parse("{\"a\":1,\"b\":null}");using var local=JsonDocument.Parse("{\"b\":null,\"a\":1.0}");Assert.Equal(StashFieldStatus.Equal,Assert.Single(StashFieldConflicts.Compare([new("profile",source.RootElement,local.RootElement)])).Status);Assert.Equal(StashFieldStatus.New,Assert.Single(StashFieldConflicts.Compare([new("members",J(new[]{1,2}),J(new[]{2,1}))])).Status);Assert.Equal(StashFieldStatus.Equal,Assert.Single(StashFieldConflicts.Compare([new("description",J<string?>(null),J<string?>(null))])).Status);
 }
 [Fact]
 public void ReturnedFieldStateOwnsItsJsonAfterCallerDocumentsAreDisposed()
 {
  IReadOnlyList<StashFieldBaseline> recorded;using(var document=JsonDocument.Parse("{\"name\":\"keep\"}"))recorded=StashFieldConflicts.Record([new("profile",document.RootElement,J<string?>(null))],[new("profile",StashFieldDecision.UseSource)]);Assert.Equal("keep",recorded[0].Source.GetProperty("name").GetString());Assert.Equal("keep",recorded[0].Local.GetProperty("name").GetString());
 }
 [Fact]
 public void AmbiguousFieldNamesUnknownDecisionsAndUndefinedValuesAreRejected()
 {
  StashFieldObservation value=new("title",J("source"),J("local"));Assert.Throws<ArgumentException>(()=>StashFieldConflicts.Compare([value,value]));Assert.Throws<ArgumentException>(()=>StashFieldConflicts.Record([value],[new("missing",StashFieldDecision.UseSource)]));Assert.Throws<ArgumentException>(()=>StashFieldConflicts.Record([value],[new("title",StashFieldDecision.KeepLocal),new("title",StashFieldDecision.UseSource)]));Assert.Throws<ArgumentException>(()=>StashFieldConflicts.Compare([value with{Source=default}]));Assert.Throws<ArgumentException>(()=>StashFieldConflicts.Compare([value],[new("title",J("a"),J("b"),(StashFieldDecision)99)]));
 }
}
