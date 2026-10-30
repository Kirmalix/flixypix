using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FlixyPix.Desktop;

/// <summary>Plays a short clip silently in a loop, for preview clips on tiles and walls. A player exists only while a
/// source is set; clearing the source closes it, so idle tiles hold no decoder.</summary>
public sealed class LoopingClip : ContentControl
{
 public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(Uri), typeof(LoopingClip),
  new PropertyMetadata(null, (owner, change) => ((LoopingClip)owner).Load(change.NewValue as Uri)));

 public static readonly DependencyProperty StretchProperty=DependencyProperty.Register(nameof(Stretch),typeof(Stretch),typeof(LoopingClip),new PropertyMetadata(Stretch.Uniform,(d,e)=> {if(((LoopingClip)d).Content is MediaElement player)player.Stretch=(Stretch)e.NewValue;}));
 public Stretch Stretch {get=>(Stretch)GetValue(StretchProperty);set=>SetValue(StretchProperty,value);}
 public Uri? Source { get => (Uri?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

 /// <summary>Set once the clip has started playing (also for tests).</summary>
 internal bool Playing { get; private set; }

 public LoopingClip() { IsHitTestVisible = false; Focusable = false; Unloaded += (_, _) => Load(null); }

 private void Load(Uri? source)
 {
  if (Content is MediaElement old) { old.Stop(); old.Close(); Content = null; }
  Playing = false;
  if (source == null) return;
  var player = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, IsMuted = true, Stretch = Stretch, Source = source };
  player.MediaOpened += (_, _) => Playing = true;
  player.MediaEnded += (_, _) => { player.Position = TimeSpan.Zero; player.Play(); };
  // A clip that cannot be played leaves the thumbnail or frame below visible.
  player.MediaFailed += (_, _) => { if (ReferenceEquals(Content, player)) Content = null; };
  Content = player;
  player.Play();
 }
}
