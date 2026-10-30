using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;
public sealed class JumpMarkTests
{
 private static readonly Guid Media=Guid.NewGuid();
 private static IReadOnlyList<JumpMark> Sample()=>JumpMarks.Build(
  [
   // Display order deliberately differs from time order, as after reordering in the details panel.
   new Bookmark { MediaId=Media,Title="Ende",PositionSeconds=300,DisplayOrder=0 },
   new Bookmark { MediaId=Media,Title="",PositionSeconds=65,DisplayOrder=1 },
   new Bookmark { MediaId=Media,Title="Alt",PositionSeconds=120,Kind=BookmarkKind.SceneThumbnail,DisplayOrder=2 },
  ],
  [new Scene { MediaId=Media,Title="Anfang",StartSeconds=10,EndSeconds=40 }],120*60);

 [Fact]
 public void MarksFollowTheTimelineAndGetReadableFallbackTitles()
 {
  var marks=Sample();
  Assert.Equal([10d,65,120,300],marks.Select(m=>m.PositionSeconds));
  Assert.Equal([JumpMarkKind.Scene,JumpMarkKind.Bookmark,JumpMarkKind.SceneThumbnail,JumpMarkKind.Bookmark],marks.Select(m=>m.Kind));
  Assert.Equal("Lesezeichen 1:05",marks[1].Title);
  Assert.Equal(40,marks[0].EndSeconds);
  Assert.Equal("5:00",marks[3].TimeLabel);
  Assert.Equal("1:01:01",JumpMarks.FormatTime(3661.9));
 }

 [Fact]
 public void NextAndPreviousSkipTheMarkAtThePlayheadLikeTrackButtons()
 {
  var marks=Sample();
  Assert.Equal(10,JumpMarks.Next(marks,0)!.PositionSeconds);
  Assert.Equal(120,JumpMarks.Next(marks,65.2)!.PositionSeconds);
  Assert.Null(JumpMarks.Next(marks,300));
  Assert.Equal(10,JumpMarks.Previous(marks,66)!.PositionSeconds);
  Assert.Equal(65,JumpMarks.Previous(marks,70)!.PositionSeconds);
  Assert.Null(JumpMarks.Previous(marks,11));
  Assert.Equal(65,JumpMarks.Current(marks,100)!.PositionSeconds);
  Assert.Null(JumpMarks.Current(marks,5));
  Assert.Equal(65,JumpMarks.Current(marks,64.8)!.PositionSeconds);
  Assert.Equal(10,JumpMarks.Current(marks,64.6)!.PositionSeconds);
 }

 [Fact]
 public void DigitsSelectMarksInTimeOrderAndInvalidPositionsAreDropped()
 {
  var marks=Sample();
  Assert.Equal(10,JumpMarks.Numbered(marks,1)!.PositionSeconds);
  Assert.Equal(300,JumpMarks.Numbered(marks,4)!.PositionSeconds);
  Assert.Null(JumpMarks.Numbered(marks,5));
  Assert.Null(JumpMarks.Numbered(marks,0));
  var trimmed=JumpMarks.Build([new Bookmark { MediaId=Media,PositionSeconds=90 },new Bookmark { MediaId=Media,PositionSeconds=double.NaN }],[],durationSeconds:60);
  Assert.Empty(trimmed);
 }

 [Fact]
 public void PlayerNavigationWrapsOnlyWhenRequested()
 {
  var marks=Sample();
  Assert.Equal(10,JumpMarks.Next(marks,300,wrap:true)!.PositionSeconds);
  Assert.Equal(300,JumpMarks.Previous(marks,0,wrap:true)!.PositionSeconds);
  Assert.Equal(300,JumpMarks.Previous(marks,11,wrap:true)!.PositionSeconds);
  Assert.Equal(120,JumpMarks.Next(marks,65.2,wrap:true)!.PositionSeconds);
  Assert.Null(JumpMarks.Next([],0,wrap:true)); Assert.Null(JumpMarks.Previous([],0,wrap:true));
  var one=new[]{marks[0]};
  Assert.Equal(one[0],JumpMarks.Next(one,10,wrap:true));
  Assert.Equal(one[0],JumpMarks.Previous(one,10,wrap:true));
  Assert.Null(JumpMarks.Next(marks,300)); Assert.Null(JumpMarks.Previous(marks,0));
 }

 [Fact]
 public void BookmarkNavigationHasDefaultKeys()
 {
  var defaults=PlayerActions.Defaults();
  Assert.Equal("PageDown",defaults[PlayerActions.NextBookmark]);
  Assert.Equal("PageUp",defaults[PlayerActions.PreviousBookmark]);
  CatalogRepository.ValidatePlayback(new PlaybackPreferences());
 }
}
