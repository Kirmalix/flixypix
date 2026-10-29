using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>O counter in the player (button and shortcuts) and what of the video was seen, for the playback history.</summary>
internal sealed partial class VideoPlayerWindow
{
 private readonly Button oButton = Labeled("", "O", T("O counter: a click adds one, the right mouse button takes the last one back"));
 private PlaybackCoverage? coverage;
 /// <summary>Parts of the video seen in this window; the library notes it in the history when playback ends.</summary>
 internal PlaybackCoverage Coverage => coverage ??= new(media.DurationSeconds);
 internal long OCount { get; private set; }
 /// <summary>Tells the library that the medium changed (O counter), so its details follow.</summary>
 internal Action<MediaItem>? MediaChanged { get; set; }
 private bool OCounterShown => repository.GetPreferences().History.ShowOCounter;

 private Button BuildOCounter()
 {
  OCount = media.OCount ?? 0; ShowOCount();
  oButton.Click += (_, _) => ChangeO(add: true);
  var undo = new MenuItem { Header = T("Take back the last O") }; undo.Click += (_, _) => ChangeO(add: false);
  oButton.ContextMenu = new ContextMenu { Items = { undo } };
  oButton.ContextMenuOpening += (_, _) => undo.IsEnabled = OCount > 0;
  oButton.Visibility = OCounterShown ? Visibility.Visible : Visibility.Collapsed;
  return oButton;
 }

 private void ShowOCount()
 {
  if (oButton.Content is StackPanel { Children: [_, TextBlock text] }) text.Text = "O " + OCount.ToString("N0", CultureInfo.CurrentCulture);
 }

 /// <summary>Adds one to the O counter or takes the last one back; a hidden counter ignores its shortcuts.</summary>
 internal void ChangeO(bool add)
 {
  if (!OCounterShown) return;
  var item = add ? repository.AddO(media.Id, DateTimeOffset.Now) : repository.UndoO(media.Id);
  if (item == null) return;
  OCount = item.OCount ?? 0; ShowOCount();
  Notify(add ? T("O counter · {0:N0}", OCount) + KeyHint(PlayerActions.UndoO) : T("Last O taken back · {0:N0}", OCount));
  MediaChanged?.Invoke(item);
 }
}
