using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed record PlayerTransform(double PositionSeconds,double X,double Y,double Width,double Height);
public sealed record PlayerTransformTimeline
{
 public bool Enabled { get; init; }=true;
 public List<PlayerTransform> Steps { get; init; }=[];
 public static PlayerTransform Identity { get; }=new(0,0,0,1,1);
 public PlayerTransform At(double positionSeconds)
 {
  if(!double.IsFinite(positionSeconds) || positionSeconds<0)throw new ArgumentOutOfRangeException(nameof(positionSeconds));
  return Enabled?Steps.LastOrDefault(step=>step.PositionSeconds<=positionSeconds)??Identity:Identity;
 }
 public static PlayerTransform FromLegacy(double seconds,double x,double y,double width,double height,double displayWidth,double displayHeight)
 {
  if(!double.IsFinite(displayWidth) || !double.IsFinite(displayHeight) || displayWidth<=0 || displayHeight<=0)throw new ArgumentException("Original display dimensions must be positive.");
  var step=new PlayerTransform(seconds,x/displayWidth,y/displayHeight,width/displayWidth,height/displayHeight); ValidateStep(step); return step;
 }
 public static PlayerTransformTimeline Validate(PlayerTransformTimeline timeline)
 {
  ArgumentNullException.ThrowIfNull(timeline); if(timeline.Steps==null || timeline.Steps.Count>4096)throw new ArgumentException("A transform timeline supports at most 4096 steps.");
  foreach(var step in timeline.Steps)ValidateStep(step);
  var ordered=timeline.Steps.OrderBy(step=>step.PositionSeconds).ToList();
  if(ordered.Select(step=>step.PositionSeconds).Distinct().Count()!=ordered.Count)throw new ArgumentException("Each transform time must be unique.");
  return timeline with { Steps=ordered };
 }
 private static void ValidateStep(PlayerTransform step)
 {
  ArgumentNullException.ThrowIfNull(step);
  if(!double.IsFinite(step.PositionSeconds) || step.PositionSeconds<0 || !double.IsFinite(step.X) || !double.IsFinite(step.Y) || !double.IsFinite(step.Width) || !double.IsFinite(step.Height) ||
   step.X is < -16 or > 16 || step.Y is < -16 or > 16 || step.Width is < .01 or > 16 || step.Height is < .01 or > 16)
   throw new ArgumentException("Transform time and normalized geometry must be finite; position ±16, size 0.01–16.");
 }
}

public sealed partial class CatalogRepository
{
 public const string PlayerTransformsKey="recon.playerTransforms";
 public PlayerTransformTimeline GetPlayerTransforms(Guid mediaId)
 {
  var item=GetMedia(mediaId)??throw new ArgumentException("Unknown media.",nameof(mediaId));
  if(!item.Extra.TryGetValue(PlayerTransformsKey,out var json) || string.IsNullOrWhiteSpace(json))return new();
  try { return PlayerTransformTimeline.Validate(JsonSerializer.Deserialize<PlayerTransformTimeline>(json,Json)??throw new InvalidDataException("Invalid transform timeline.")); }
  catch(Exception error) when(error is JsonException or ArgumentException) { throw new InvalidDataException("The stored player transform timeline is invalid.",error); }
 }
 public PlayerTransformTimeline SavePlayerTransforms(Guid mediaId,PlayerTransformTimeline timeline)
 {
  var validated=PlayerTransformTimeline.Validate(timeline);
  lock(gate)
  {
   ThrowIfDisposed(); var item=GetMedia(mediaId)??throw new ArgumentException("Unknown media.",nameof(mediaId));
   if(item.Kind!=MediaKind.Video)throw new ArgumentException("Player transforms apply to video media.");
   Upsert(item with { Extra=new(item.Extra) { [PlayerTransformsKey]=JsonSerializer.Serialize(validated,Json) } }); return validated;
  }
 }
 private void ImportLegacyPlayerTransforms(Func<string,IEnumerable<JsonElement>> rows,LegacyMediaMap media,SqliteTransaction transaction,List<string> warnings)
 {
  var grouped=new Dictionary<Guid,List<PlayerTransform>>(); var ordinal=0;
  foreach(var values in rows("videotransform"))
  {
   ordinal++;
   if(!media.TryGetValue("movies:"+Text(values,"videoID"),out var item)) { warnings.Add($"videotransform/{ordinal}: video reference unresolved; original row retained."); continue; }
   try
   {
    double Required(string field)=>Number(values,field)??throw new ArgumentException("Missing transform field: "+field);
    var step=PlayerTransformTimeline.FromLegacy(Required("secs"),Required("boundsx"),Required("boundsy"),Required("boundsw"),Required("boundsh"),Required("displayw"),Required("displayh"));
    if(!grouped.TryGetValue(item.Id,out var steps))grouped[item.Id]=steps=[];
    if(steps.Any(s=>s.PositionSeconds==step.PositionSeconds))throw new ArgumentException("Duplicate transform time; ambiguous row retained without projection.");
    steps.Add(step);
   }
   catch(ArgumentException error) { warnings.Add($"videotransform/{ordinal}: {error.Message} Original row retained."); }
  }
  foreach(var pair in grouped)
  {
   var item=media.Values.First(m=>m.Id==pair.Key); var timeline=PlayerTransformTimeline.Validate(new() { Steps=pair.Value });
   if(timeline.Steps[0].PositionSeconds>0)timeline.Steps.Insert(0,PlayerTransformTimeline.Identity);
   var updated=UpsertCore(item with { Extra=new(item.Extra) { [PlayerTransformsKey]=JsonSerializer.Serialize(timeline,Json) } },transaction);
   media[item.LegacyId!]=updated;
  }
 }
}
