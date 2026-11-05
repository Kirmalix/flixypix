using FlixyPix.Core;
using System.Windows.Media;
using Xunit;

namespace FlixyPix.Desktop.Tests;

public sealed class MediaCardContextTests
{
 [Fact]
 public void DifferentViewsHaveIndependentPreferencesAndMemberships()
 {
  var item = new MediaItem {Path="video.mp4",Title="Video"};
  var first = new MediaCardContext {Display=new() {CropThumbnails=true},FinishedMembers=[item.Id]};
  var second = new MediaCardContext();
  var a = new MediaCard(item,false,context:first);var b=new MediaCard(item,false,context:second);
  Assert.Equal(Stretch.UniformToFill,a.ThumbnailStretch);Assert.Equal(Stretch.Uniform,b.ThumbnailStretch);
  Assert.True(a.IsFinished);Assert.False(b.IsFinished);
  second.QuickMembers.Add(item.Id);
  Assert.NotEqual(a.QuickGlyph,b.QuickGlyph);
 }
}
