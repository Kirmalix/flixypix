using System.Text.Json;

namespace FlixyPix.Core;

public enum StashFieldDecision { KeepLocal,UseSource }
public enum StashFieldStatus { Equal,New,SourceChanged,SourceUnchanged,PreviouslyKept,Conflict }
public sealed record StashFieldObservation(string Field,JsonElement Source,JsonElement Local);
public sealed record StashFieldBaseline(string Field,JsonElement Source,JsonElement Local,StashFieldDecision Decision);
public sealed record StashFieldChoice(string Field,StashFieldDecision Decision);
public sealed record StashFieldDiff(string Field,JsonElement Source,JsonElement Local,StashFieldBaseline? Previous,StashFieldStatus Status)
{
 public StashFieldDecision DefaultDecision=>StashFieldDecision.KeepLocal;
}

/// <summary>Three-way field comparison. Missing source fields never imply local deletion; target writes belong to the guarded apply transaction.</summary>
public static class StashFieldConflicts
{
 public static IReadOnlyList<StashFieldDiff> Compare(IReadOnlyList<StashFieldObservation> observations,IReadOnlyList<StashFieldBaseline>? previous=null)
 {
  ArgumentNullException.ThrowIfNull(observations);previous??=[];Validate(previous);
  if(observations.Count>256||observations.Any(value=>value==null)||observations.Select(value=>value.Field).Distinct(StringComparer.Ordinal).Count()!=observations.Count)throw new ArgumentException("Invalid or duplicate Stash field observations.");
  var baselines=previous.ToDictionary(value=>value.Field,StringComparer.Ordinal);var result=new List<StashFieldDiff>();
  foreach(var observation in observations)
  {
   ValidateValue(observation.Field,observation.Source,observation.Local);baselines.TryGetValue(observation.Field,out var before);
   var status=Same(observation.Source,observation.Local)?StashFieldStatus.Equal:before==null?StashFieldStatus.New:
    Same(observation.Source,before.Source)?before.Decision==StashFieldDecision.KeepLocal?StashFieldStatus.PreviouslyKept:StashFieldStatus.SourceUnchanged:
    !Same(observation.Local,before.Local)?StashFieldStatus.Conflict:StashFieldStatus.SourceChanged;
   result.Add(new(observation.Field,observation.Source.Clone(),observation.Local.Clone(),before is null?null:before with{Source=before.Source.Clone(),Local=before.Local.Clone()},status));
  }
  return result;
 }
 public static IReadOnlyList<StashFieldBaseline> Record(IReadOnlyList<StashFieldObservation> observations,IReadOnlyList<StashFieldChoice> choices,IReadOnlyList<StashFieldBaseline>? previous=null)
 {
  ArgumentNullException.ThrowIfNull(choices);var differences=Compare(observations,previous).ToDictionary(value=>value.Field,StringComparer.Ordinal);
  if(choices.Count>256||choices.Any(choice=>choice==null||!Enum.IsDefined(choice.Decision)||!differences.ContainsKey(choice.Field))||choices.Select(choice=>choice.Field).Distinct(StringComparer.Ordinal).Count()!=choices.Count)throw new ArgumentException("Invalid or duplicate Stash field decisions.");
  var result=(previous??[]).ToDictionary(value=>value.Field,value=>value with{Source=value.Source.Clone(),Local=value.Local.Clone()},StringComparer.Ordinal);
  foreach(var choice in choices)
  {
   var difference=differences[choice.Field];result[choice.Field]=new(choice.Field,difference.Source.Clone(),(choice.Decision==StashFieldDecision.UseSource?difference.Source:difference.Local).Clone(),choice.Decision);
  }
  var values=result.OrderBy(pair=>pair.Key,StringComparer.Ordinal).Select(pair=>pair.Value).ToArray();Validate(values);return values;
 }
 internal static bool Same(JsonElement left,JsonElement right)=>JsonElement.DeepEquals(left,right);
 internal static void Validate(IReadOnlyList<StashFieldBaseline> values)
 {
  if(values==null||values.Count>256||values.Any(value=>value==null||!Enum.IsDefined(value.Decision))||values.Select(value=>value.Field).Distinct(StringComparer.Ordinal).Count()!=values.Count)throw new ArgumentException("Invalid Stash field baselines.");
  foreach(var value in values)ValidateValue(value.Field,value.Source,value.Local);
 }
 private static void ValidateValue(string field,JsonElement source,JsonElement local)
 {
  if(string.IsNullOrWhiteSpace(field)||field.Length>256||field.Any(char.IsControl)||source.ValueKind==JsonValueKind.Undefined||local.ValueKind==JsonValueKind.Undefined)throw new ArgumentException("Invalid Stash field value.");
 }
}
