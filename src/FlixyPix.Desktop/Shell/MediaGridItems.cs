using System.Collections.ObjectModel;
using System.Windows.Controls;

namespace FlixyPix.Desktop;

/// <summary>Apply a refreshed page without resetting the item generator and all its loaded thumbnails.</summary>
internal static class MediaGridItems
{
 internal static void Publish(ListBox list,IReadOnlyList<MediaCard> next,bool retain)
 {
  if(!retain || list.ItemsSource is not ObservableCollection<MediaCard> items)
  {
   list.ItemsSource=new ObservableCollection<MediaCard>(next);
   return;
  }
  // Collections can contain the same medium more than once. Reconcile occurrences in their displayed order.
  var counts=next.GroupBy(c=>c.Item.Id).ToDictionary(g=>g.Key,g=>g.Count());
  var seen=new Dictionary<Guid,int>();
  for(var i=0;i<items.Count;)
  {
   var id=items[i].Item.Id;var occurrence=seen.GetValueOrDefault(id)+1;seen[id]=occurrence;
   if(occurrence>counts.GetValueOrDefault(id))items.RemoveAt(i);else i++;
  }
  var existing=items.GroupBy(c=>c.Item.Id).ToDictionary(g=>g.Key,g=>new Queue<MediaCard>(g));
  for(var i=0;i<next.Count;i++)
  {
   var card=next[i];
   if(existing.TryGetValue(card.Item.Id,out var occurrences) && occurrences.TryDequeue(out var kept))
   {
    var position=items.IndexOf(kept);
    if(position!=i)items.Move(position,i);
    if(kept.CanReuse(card))kept.UpdateFrom(card);else items[i]=card;
   }
   else items.Insert(i,card);
  }
 }
}
