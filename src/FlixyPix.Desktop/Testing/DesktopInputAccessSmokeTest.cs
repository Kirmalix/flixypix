using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FlixyPix.Core;
namespace FlixyPix.Desktop;
public partial class MainWindow
{
 private async Task RunInputAccessSmokeTestAsync(string directory)
 {
  static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
  async Task Idle()=>await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
  async Task SendKey(UIElement target,Key key,ModifierKeys modifiers=ModifierKeys.None)
  {
   var old=new byte[256];GetKeyboardState(old);var state=(byte[])old.Clone();
   foreach(var code in new[]{0x10,0x11,0x12,0xA0,0xA2})state[code]=0;
   if(modifiers.HasFlag(ModifierKeys.Control))state[0x11]=state[0xA2]=128;
   if(modifiers.HasFlag(ModifierKeys.Shift))state[0x10]=state[0xA0]=128;
   try {SetKeyboardState(state);target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(target)!,0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent});await Idle();await _windowShift;await Idle();}
   finally {SetKeyboardState(old);}
  }
  var before=_repository.GetPreferences();var view=CaptureViewState();var ids=new List<Guid>();
  try {
   var tree=(IEnumerable<BrowseNode>)Folders.ItemsSource;var root=tree.First();var children=root.Children.ToList();
   try {
    for(int i=0;i<40;i++)root.Children.Add(new BrowseNode("wheel-"+i,Path.Combine(directory,"wheel",i.ToString())));
    root.IsExpanded=true;Folders.Items.Refresh();await Idle();FolderViewport.ScrollToVerticalOffset(0);await Idle();
    var row=(TreeViewItem)Folders.ItemContainerGenerator.ContainerFromIndex(0);
    void Wheel(int delta)=>row.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,0,delta){RoutedEvent=Mouse.PreviewMouseWheelEvent});
    Wheel(-60);await Idle();Check(FolderViewport.VerticalOffset==0,"Partial wheel moved too early.");Wheel(-60);await Idle();Check(FolderViewport.VerticalOffset>0,"Mouse wheel over folder row did not move the visible viewport.");
    Wheel(120);await Idle();Check(FolderViewport.VerticalOffset==0,"Reverse wheel did not return to top.");
   } finally {root.Children.Clear();root.Children.AddRange(children);Folders.Items.Refresh();}
   ApplyViewState(new());await _lastRefresh;await Idle();
   Check(new MediaCard(new(){Path="fit.mp4"}).ThumbnailStretch==Stretch.Uniform,"Default thumbnails crop.");
   ThumbnailFit.SelectedIndex=1;await _lastRefresh;Check(_repository.GetPreferences().General.CropThumbnails&&new MediaCard(new(){Path="fill.mp4"}).ThumbnailStretch==Stretch.UniformToFill,"Crop setting not applied.");ThumbnailFit.SelectedIndex=0;await _lastRefresh;
   var first=MediaList.Items.Cast<MediaCard>().First();MediaList.SelectedItem=first;await Idle();
   var aspect=Visuals<AspectBox>((ListBoxItem)MediaList.ItemContainerGenerator.ContainerFromItem(first)).First();Check(Math.Abs(aspect.Ratio-9d/16)<.001,"Grid ratio is not 16:9.");
   var help=new HelpWindow(this,T("Help"),HelpWindow.Guide());help.Show();await Idle();Capture((FrameworkElement)help.Content,Path.Combine(directory,"help-offline.png"));help.Close();
   await SendKey(MediaList,Key.F10);Check(_applicationMenu?.IsOpen==true,"F10 did not open app menu.");Capture(RootContent,Path.Combine(directory,"input-grid.png"));Capture(_applicationMenu!,Path.Combine(directory,"application-menu.png"));_applicationMenu!.IsOpen=false;
   Check(!TechnicalInformation().Contains(_settings.DataDirectory),"Technical information contains a library path.");
   for(int i=0;i<680;i++){var item=_repository.Upsert(new(){Path=Path.Combine(directory,"kbd-"+i.ToString("D4")+".mp4"),Title="kbd-"+i.ToString("D4"),Kind=MediaKind.Video});ids.Add(item.Id);}
   ApplyViewState(new(){SearchText="kbd-"});await _lastRefresh;await SelectIndexAsync(0);await Idle();
   await SendKey(MediaList,Key.End);for(int i=0;i<40&&_keyboardIndex!=679;i++)await Task.Delay(25);
   Check(_keyboardIndex==679&&_windowStart>0,"End did not navigate past the loaded window.");
   await SendKey(MediaList,Key.Left,ModifierKeys.Shift);for(int i=0;i<40&&SelectedMedia.Count!=2;i++)await Task.Delay(25);
   Check(SelectedMedia.Count==2,"Shift navigation did not extend selection.");
   await SendKey(MediaList,Key.Home,ModifierKeys.Shift);for(int i=0;i<40&&SelectedMedia.Count!=680;i++)await Task.Delay(25);
   Check(SelectedMedia.Count==680&&_offWindowSelection.Count>0,"Shift+Home did not preserve a selection across loaded windows.");
   await SendKey(MediaList,Key.Right,ModifierKeys.Control);for(int i=0;i<40&&_keyboardIndex!=1;i++)await Task.Delay(25);
   Check(SelectedMedia.Count==680,"Ctrl+arrow changed selection.");
   await SendKey(MediaList,Key.Home);for(int i=0;i<40&&_keyboardIndex!=0;i++)await Task.Delay(25);Check(_keyboardIndex==0,"Home did not navigate to the beginning.");
   MediaList.Focus();await SendKey(MediaList,Key.F6);Check(!MediaList.IsKeyboardFocusWithin,"F6 did not leave media region.");
  }
  finally {foreach(var id in ids)_repository.Delete(id);_keyboardIndex=_keyboardAnchor=null;_repository.SavePreferences(before);ApplyViewState(view);await _lastRefresh;}
 }
}
