using FlixyPix.Core;

namespace FlixyPix.Desktop;

internal sealed record WorklistRequest(long Generation, ViewState View, Guid[] Kept, Guid[] Listed);

/// <summary>Owns retained members. An older query can never publish into a newer list.</summary>
internal sealed class WorklistState
{
 internal HashSet<Guid> Kept { get; } = [];
 internal ViewState? Shown { get; set; }
 internal bool DropNext { get; set; }
 internal bool IgnoreLeavingOnce { get; set; }
 private ViewState? view;
 private long generation;
 private static bool IsWorklist(ViewState state) => state.Filter.HasThumbnail is not null || state.Filter.UnratedOnly || state.Filter.UntaggedOnly;
 private static bool SameList(ViewState? known, ViewState state) => known is not null && known.SameView(state with { SortOrder=known.SortOrder });

 internal WorklistRequest Begin(ViewState state, IEnumerable<Guid> listed, long requestGeneration)
 {
  generation = requestGeneration;
  var same = SameList(view,state); var drop = DropNext; var ignore = IgnoreLeavingOnce;
  var kept = same && !drop && IsWorklist(state) ? Kept.ToArray() : [];
  var leaving = same && !drop && !ignore && IsWorklist(state) && SameList(Shown,state)
   ? listed.Except(kept).ToArray() : [];
  return new(requestGeneration,state,kept,leaving);
 }

 internal bool Commit(WorklistRequest request, IReadOnlySet<Guid> matching)
 {
  if (request.Generation != generation) return false;
  DropNext = IgnoreLeavingOnce = false;
  view = request.View;
  Kept.Clear(); Kept.UnionWith(request.Kept);
  Kept.UnionWith(request.Listed.Where(id => !matching.Contains(id)));
  return true;
 }
}
