using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class NavigationTests : IDisposable
{
 private readonly string directory=Path.Combine(Path.GetTempPath(),"flixypix-navigation-"+Guid.NewGuid().ToString("N"));
 [Fact]
 public void PositionUsesQueryFiltersAndStableSortWithoutLoadingDocuments()
 {
  using var catalog=new CatalogRepository(Path.Combine(directory,"catalog.db"));
  var first=catalog.Upsert(new(){Path=Path.Combine(directory,"a.jpg"),Title="A",Kind=MediaKind.Image});
  var second=catalog.Upsert(new(){Path=Path.Combine(directory,"b.jpg"),Title="B",Kind=MediaKind.Image});
  var movie=catalog.Upsert(new(){Path=Path.Combine(directory,"movie.mp4"),Title="C",Kind=MediaKind.Video});
  var query=new MediaQuery{Kind=MediaKind.Image,Descending=true};
  Assert.Equal(0,catalog.GetPosition(query,second.Id)); Assert.Equal(1,catalog.GetPosition(query,first.Id)); Assert.Null(catalog.GetPosition(query,movie.Id));
 }
 [Fact]
 public void DescendingPositionMatchesPageOrderWhenTitlesAndRatingsTie()
 {
  using var catalog=new CatalogRepository(Path.Combine(directory,"catalog.db"));
  for(var i=0;i<8;i++)catalog.Upsert(new(){Path=Path.Combine(directory,$"{i}.jpg"),Title="Same",Rating=8});
  foreach(var sort in new[]{MediaSort.Title,MediaSort.Rating,MediaSort.Path})
  {
   var query=new MediaQuery{Sort=sort,Descending=true};var page=catalog.QueryPage(query);
   for(var i=0;i<page.Items.Count;i++)Assert.Equal(i,catalog.GetPosition(query,page.Items[i].Id));
  }
 }
 [Fact]
 public void PlaylistPagesPreserveRepeatedItemsOrderingAndFilters()
 {
  using var catalog=new CatalogRepository(Path.Combine(directory,"catalog.db"));
  var first=catalog.Upsert(new(){Path=Path.Combine(directory,"a.jpg"),Title="A",Rating=9});
  var second=catalog.Upsert(new(){Path=Path.Combine(directory,"b.jpg"),Title="B",Rating=1});
  var playlist=catalog.UpsertPlaylist(new(){Name="Repeated",MediaIds=[second.Id,first.Id,second.Id,first.Id]});
  var page=catalog.QueryPlaylistPage(playlist,new(){Offset=1,Limit=2});
  Assert.Equal(4,page.Page.TotalCount); Assert.Equal(new[]{first.Id,second.Id},page.Page.Items.Select(i=>i.Id));
  var filtered=catalog.QueryPlaylistPage(playlist,new(){MinimumRating=8,Limit=1},selectedId:first.Id);
  Assert.Equal(2,filtered.Page.TotalCount); Assert.Equal(0,filtered.Offset); Assert.Equal(first.Id,filtered.Page.Items.Single().Id);
  Assert.Equal(first.Id,catalog.QueryPlaylistPage(playlist,new(){Limit=1},false).Page.Items.Single().Id);
 }
 public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
