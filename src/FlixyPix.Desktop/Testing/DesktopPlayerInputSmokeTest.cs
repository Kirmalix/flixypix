using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 [DllImport("user32.dll")] private static extern nint SendMessage(nint window,uint message,nint wParam,nint lParam);
 [DllImport("user32.dll")] private static extern nint ChildWindowFromPointEx(nint parent,NativeInputPoint point,uint flags);
 [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
 [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
 [StructLayout(LayoutKind.Sequential)] private struct NativeInputPoint { public int X,Y; }
 private void NativePlayerMessage(uint message,nint wParam=0) => SendMessage(_videoWindow!.VideoHandle,message,wParam,(nint)((20<<16)|20));
 private async Task NativePlayerWheelAsync(int delta,ModifierKeys modifiers)
 {
  var saved=new byte[256]; GetKeyboardState(saved); var state=(byte[])saved.Clone();
  state[0x10]=(byte)(modifiers.HasFlag(ModifierKeys.Shift)?128:0);
  state[0x11]=(byte)(modifiers.HasFlag(ModifierKeys.Control)?128:0);
  state[0x12]=(byte)(modifiers.HasFlag(ModifierKeys.Alt)?128:0);
  try { SetKeyboardState(state); NativePlayerMessage(0x020A,(nint)(((long)(ushort)(short)delta<<16) | (modifiers.HasFlag(ModifierKeys.Control)?8L:0L) | (modifiers.HasFlag(ModifierKeys.Shift)?4L:0L))); }
  finally { SetKeyboardState(saved); }
  await Task.Delay(100);
 }
 private async Task CheckNativePlayerInputAsync()
 {
  var window=_videoWindow!;
  if(ChildWindowFromPointEx(window.VideoHandle,new NativeInputPoint { X=20,Y=20 },2)!=window.VideoHandle)
   throw new InvalidOperationException("The disabled mpv child intercepts native hit testing.");
  if(SendMessage(window.VideoHandle,0x0084,0,0)!=1)
   throw new InvalidOperationException("The native video input surface is not hit-testable.");
  var before=window.IsPaused;
  NativePlayerMessage(0x0201); NativePlayerMessage(0x0202); await Task.Delay(1100);
  if(window.IsPaused==before)throw new InvalidOperationException("A native single click did not toggle pause.");
  NativePlayerMessage(0x0201); NativePlayerMessage(0x0202); await Task.Delay(1100);
  if(window.IsPaused!=before)throw new InvalidOperationException("A second native single click did not restore pause.");
  var volume=window.Volume;
  await NativePlayerWheelAsync(-60,ModifierKeys.Control);
  if(window.Volume!=volume)throw new InvalidOperationException("A partial wheel delta triggered a whole step.");
  await NativePlayerWheelAsync(-60,ModifierKeys.Control);
  if(Math.Abs(window.Volume-(volume-5))>.01)throw new InvalidOperationException("Partial wheel deltas were lost.");
  await NativePlayerWheelAsync(120,ModifierKeys.Control);
  SendMessage(window.VideoOuterHandle,0x0208,0,0); await Task.Delay(100);
  if(window.Volume!=0)throw new InvalidOperationException("The outer native host did not receive middle click.");
  SendMessage(window.VideoOuterHandle,0x0208,0,0); await Task.Delay(100);
  NativePlayerMessage(0x0205); await Task.Delay(100);
  if(window.OpenPlayerMenu?.IsOpen!=true)throw new InvalidOperationException("Native right click did not open the player menu.");
  window.OpenPlayerMenu.IsOpen=false;
  var stepRequested=window.StepRequested; var steps=new List<int>();
  window.StepRequested=step=> { steps.Add(step); return Task.CompletedTask; };
  try
  {
   NativePlayerMessage(0x020C,(nint)(1<<16)); NativePlayerMessage(0x020C,(nint)(2<<16)); await Task.Delay(100);
   if(!steps.SequenceEqual(new[]{-1,1}))throw new InvalidOperationException("Side buttons did not navigate in order.");
  }
  finally { window.StepRequested=stepRequested; }
  if(Keyboard.FocusedElement!=window)throw new InvalidOperationException("Native clicks did not return keyboard focus to the player.");
 }
 private async Task CheckPlayerEditingAndVolumeAsync()
 {
  static void Check(bool ok,string message) { if(!ok)throw new InvalidOperationException(message); }
  var window=_videoWindow!; var volume=window.Volume; var originalPause=window.IsPaused;
  if(!originalPause)await window.TogglePauseAsync();
  Check(window.VolumeControl.IsMoveToPointEnabled,"Volume clicks still use relative large steps.");
  window.VolumeControl.Value=63;
  window.MuteControl.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
  Check(window.Volume==0 && (string)window.MuteControl.Content=="\uE74F","Mute did not show the muted icon.");
  window.MuteControl.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
  Check(window.Volume==63 && (string)window.MuteControl.Content=="\uE767","Unmute lost the previous volume.");
  window.VolumeControl.Value=0; window.ToggleMute(); Check(window.Volume==63,"Manual zero lost the last positive level.");
  window.ToggleMute(); window.VolumeControl.Value=27; window.ToggleMute(); window.ToggleMute();
  Check(window.Volume==27,"Changing volume while muted did not replace the remembered level.");
  var bookmark=_repository.GetBookmarks(window.MediaId).First();
  var list=window.MarkList; list.UpdateLayout();
  var pencil=Descendants<System.Windows.Controls.Button>(list).First(b=>b.Tag as string=="rename" && b.DataContext==list.Items[0]);
  var position=window.LastPositionSeconds;
  pencil.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
  Check(InlineEdit.Current!=null,"Pencil did not open the inline name editor.");
  var editor=InlineEdit.Current!; window.RefreshMarks();
  Check(InlineEdit.Current==editor && Math.Abs(position-window.LastPositionSeconds)<.05,"Renaming moved playback or lost the editor during refresh.");
  editor.Text="Player renamed mark";
  editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(editor)!,0,Key.Enter) { RoutedEvent=Keyboard.PreviewKeyDownEvent });
  Check(window.Marks.Any(m=>m.Title=="Player renamed mark"),"The player did not refresh the renamed title.");
  Check(_repository.GetBookmarks(window.MediaId).Any(b=>b.Title=="Player renamed mark") || _repository.GetScenes(window.MediaId).Any(s=>s.Title=="Player renamed mark"),"Renaming was not saved.");
  list.Focus();
  list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(list)!,0,Key.F2) { RoutedEvent=Keyboard.PreviewKeyDownEvent });
  Check(InlineEdit.Current!=null,"F2 did not open the name editor.");
  InlineEdit.Current!.Text="Do not keep"; InlineEdit.Cancel();
  Check(!window.Marks.Any(m=>m.Title=="Do not keep"),"Cancelling renamed the mark.");
  var mark=window.Marks.First(m=>m.Kind==FlixyPix.Core.JumpMarkKind.Bookmark);
  list.SelectedIndex=window.Marks.ToList().IndexOf(mark); list.ScrollIntoView(list.SelectedItem); list.UpdateLayout();
  var item=(System.Windows.Controls.ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem);
  item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Right) { RoutedEvent=Mouse.PreviewMouseUpEvent });
  Check(window.OpenMarkMenu?.IsOpen==true,"Right click did not open the mark menu.");
  var rename=window.OpenMarkMenu!.Items.OfType<System.Windows.Controls.MenuItem>().First(); window.OpenMarkMenu.IsOpen=false;
  rename.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
  Check(InlineEdit.Current!=null,"The context menu did not start renaming.");
  var live=_repository.GetBookmarks(window.MediaId).Single(b=>b.Id==mark.SourceId);
  _repository.UpsertBookmark(live with { Description="Updated while renaming" }); window.RefreshMarks();
  InlineEdit.Current!.Text="Renamed bookmark";
  InlineEdit.Current.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(InlineEdit.Current)!,0,Key.Enter) { RoutedEvent=Keyboard.PreviewKeyDownEvent });
  var saved=_repository.GetBookmarks(window.MediaId).Single(b=>b.Id==mark.SourceId);
  Check(saved.Title=="Renamed bookmark" && saved.Description=="Updated while renaming","Renaming overwrote newer bookmark data.");
  list.Focus(); window.BeginRenameSelectedMark(); InlineEdit.Current!.Text="   ";
  InlineEdit.Current.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(InlineEdit.Current)!,0,Key.Enter) { RoutedEvent=Keyboard.PreviewKeyDownEvent });
  Check(_repository.GetBookmarks(window.MediaId).Single(b=>b.Id==mark.SourceId).Title=="Renamed bookmark","A blank name replaced the saved title.");
  Capture((FrameworkElement)window.Content, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_settings.DataDirectory)!,"player-controls-renamed.png"));
  window.VolumeControl.Value=volume;
  if(!originalPause)await window.TogglePauseAsync();
 }

}

internal sealed partial class VideoPlayerWindow
{
 internal Task SamplePlaybackAsync()=>PollAsync();
 internal async Task CheckKeyboardControlsAsync()
 {
  if(!volumeSlider.Focusable||!position.Focusable)throw new InvalidOperationException("Player sliders cannot receive keyboard focus.");
  var wasPaused=paused;var volume=volumeSlider.Value;
  try
  {
   if(!paused)await TogglePauseAsync();volumeSlider.Value=40;volumeSlider.Focus();
   var source=PresentationSource.FromVisual(volumeSlider)!;
   volumeSlider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Right){RoutedEvent=Keyboard.PreviewKeyDownEvent});
   volumeSlider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Right){RoutedEvent=Keyboard.KeyDownEvent});
   if(volumeSlider.Value<=40)throw new InvalidOperationException("Arrow key did not change the focused volume slider.");
  }
  finally {volumeSlider.Value=volume;if(paused!=wasPaused)await TogglePauseAsync();Focus();Keyboard.Focus(this);}
 }
}
