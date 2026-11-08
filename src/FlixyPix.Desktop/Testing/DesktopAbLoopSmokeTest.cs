using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlixyPix.Core;
using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;
public partial class MainWindow
{
 private async Task RunAbLoopControlsSmokeAsync(VideoPlayerWindow window,MpvPlayer player,CatalogRepository db,Guid mediaId,string root)
 {
  static void Require(bool value,string text){if(!value)throw new InvalidOperationException(text);}
  Button ActionButton(string label)=>Descendants<Button>((FrameworkElement)window.Content).First(button=>button.IsVisible&&Equals(System.Windows.Automation.AutomationProperties.GetName(button),T(label)));
  static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  async Task Idle()=>await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
  async Task Mark(double from,double to){await window.SeekToAsync(from);Click(ActionButton("Set range start"));await window.SeekToAsync(to);Click(ActionButton("Set range end"));await Idle();}
  var repeat=window.Repeat;window.Repeat=true;
  Require(window.AbLoop==new AbLoopState()&&window.RangeStart==null&&!Descendants<Button>((FrameworkElement)window.Content).Any(button=>button.IsVisible&&Equals(System.Windows.Automation.AutomationProperties.GetName(button),T("Save as scene"))),"Fresh window inherited a range or offers its results.");
  Require(!ActionButton("Set range end").IsEnabled,"A range end was possible without a start.");
  await Mark(1,2);Require(window.RangeStart==1&&window.RangeEnd==2&&window.AbLoop==new AbLoopState()&&(await player.GetAbLoopAsync())==new AbLoopState()&&window.Repeat,"Marks started a loop or changed playlist repeat.");
  Click(ActionButton("Loop range"));await window.AbLoopUpdate;Require(window.AbLoop is {Start:1,End:2}&&await player.GetAbLoopAsync()==window.AbLoop&&window.Repeat,"The loop did not reach actual mpv or changed playlist repeat.");
  await window.SeekToAsync(.5);Click(ActionButton("Set range end"));await window.AbLoopUpdate;Require(window.RangeEnd==2&&await player.GetAbLoopAsync()==window.AbLoop,"An invalid end replaced a valid range.");
  await window.SeekToAsync(1.5);Click(ActionButton("Set range end"));await window.AbLoopUpdate;Require(window.AbLoop is {Start:1,End:1.5}&&await player.GetAbLoopAsync()==window.AbLoop,"A running loop did not follow the range.");
  Click(ActionButton("End loop"));await window.AbLoopUpdate;Require(window.RangeEnd==1.5&&window.AbLoop==new AbLoopState()&&await player.GetAbLoopAsync()==new AbLoopState(),"Ending the loop lost the range or kept native points.");
  var sceneCount=db.GetScenes(mediaId).Count;Click(ActionButton("Save as scene"));await window.AbLoopUpdate;
  Require(db.GetScenes(mediaId).Count==sceneCount+1&&db.GetScenes(mediaId).Any(scene=>scene.StartSeconds==1&&scene.EndSeconds==1.5)&&window.RangeStart==null,"Saving saved a wrong range or kept it.");
  await Mark(3,4);Click(ActionButton("Loop range"));await window.AbLoopUpdate;Click(ActionButton("Save as scene"));await window.AbLoopUpdate;Require(window.AbLoop==new AbLoopState()&&await player.GetAbLoopAsync()==new AbLoopState()&&db.GetScenes(mediaId).Any(scene=>scene.StartSeconds==3),"Saving did not end a running loop.");
  var rating=db.GetMedia(mediaId)!.Rating;window.Rate(7);Require(db.GetMedia(mediaId)!.Rating==7&&window.Rating==7,"Player rating was not saved.");window.Rate(rating);
  await Mark(1,2);await window.SeekToAsync(5);Click(ActionButton("Set range start"));await window.AbLoopUpdate;Require(window.RangeStart==5&&window.RangeEnd==null,"A start beyond the end did not clear the end.");
  Click(ActionButton("Discard range"));await window.AbLoopUpdate;await Idle();Require(window.RangeStart==null,"Discarding kept the range.");window.Repeat=repeat;
  await Mark(.25,.75);Require(window.RangeSummary.Contains("0:00.250")&&window.RangeSummary.Contains("0:00.750"),"Sub-second range boundaries are not visible precisely.");
  Click(ActionButton("Loop range"));await window.AbLoopUpdate;Click(ActionButton("Discard range"));await window.AbLoopUpdate;await Idle();Require(window.AbLoop==new AbLoopState()&&await player.GetAbLoopAsync()==new AbLoopState(),"Discarding did not end the loop.");
  // Messages stand over the video for a moment; the bar has no status or path line any more.
  window.Act(PlayerActions.ClipEnd);Require(window.MessageShown&&window.MessageText==T("Set the start of the range first and then a later end.")+window.KeyHintOf(PlayerActions.ClipStart),"A message did not appear over the video.");
  var normalWanted=db.GetPreferences().Playback.ShowMarks;window.ToggleFullscreen();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
  Require(!window.MarkPanelVisible,"Fullscreen unexpectedly opened the list before an explicit request.");
  var menu=window.ShowPlayerMenu();var marks=menu.Items.OfType<MenuItem>().Single(item=>Equals(item.Header,T("Jump marks")));Require(marks.IsEnabled,"Fullscreen disabled mark navigation.");marks.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));menu.IsOpen=false;
  Require(window.MarkPanelVisible&&db.GetPreferences().Playback.ShowMarks==normalWanted,"Fullscreen list did not open or changed saved normal visibility.");window.RefreshMarks();Require(window.MarkPanelVisible,"Refresh hid explicitly opened fullscreen marks.");
  window.SimulateRest();Require(window.OverlayShown,"Controls hid while fullscreen marks were open.");
  await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Capture((FrameworkElement)window.Content,Path.Combine(root,"fullscreen-marks.png"));
  Capture(window.ControlsSurface,Path.Combine(root,"fullscreen-controls.png"));
  window.ToggleMarkPanel();Require(!window.MarkPanelVisible,"Fullscreen list did not close.");
  window.Act(PlayerActions.ClipStart);await Idle();Require(window.RangeStripVisible&&window.ControlLabels.Contains(T("Set range end")),"Full screen has no range row while the list is hidden.");await window.ClearRangeAsync();Require(!window.RangeStripVisible,"The range row stayed without a range.");
  window.ToggleFullscreen();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Require(window.MarkPanelVisible==normalWanted,"Leaving fullscreen lost normal panel preference.");
  // The narrow window with the range row, which only shows while the list is hidden.
  var listWanted=window.MarkPanelVisible;if(listWanted)window.ToggleMarkPanel();
  await window.SeekToAsync(1);window.Act(PlayerActions.ClipStart);await window.SeekToAsync(2);window.Act(PlayerActions.ClipEnd);
  window.Width=760;await Idle();Require(window.RangeStripVisible,"The range row is missing while the list is hidden.");
  foreach(var button in Descendants<Button>(window.ControlsSurface).Where(button=>button.IsVisible))
  {
   var point=button.TransformToAncestor(window.ControlsSurface).Transform(new Point());
   Require(point.X>=-1&&point.X+button.ActualWidth<=window.ControlsSurface.ActualWidth+1,"Narrow player clips a control: "+button.Content);
  }
  Capture(window.ControlsSurface,Path.Combine(root,"compact-controls.png"));
  await window.ClearRangeAsync();if(listWanted)window.ToggleMarkPanel();
  // The row stays one line at every size: what does not fit moves into the menu, and a larger interface keeps its room.
  double Middle(string name){var button=ActionButton(name);return button.TransformToAncestor(window.ControlsSurface).Transform(new Point(button.ActualWidth/2,0)).X-window.ControlsSurface.ActualWidth/2;}
  Require(Math.Abs(Middle("Play or pause"))<2,"The play button left the middle of a narrow bar.");
  Require(window.MoreMenuShown,"The menu of a narrow bar is missing.");
  double Line(string name)=>ActionButton(name).TransformToAncestor(window.ControlsSurface).Transform(new Point(0,ActionButton(name).ActualHeight/2)).Y;
  Require(Math.Abs(Line(T("Play or pause"))-Line(T("Full screen (F11)")))<2,"The control row wrapped.");
  Require(window.ControlsFolded&&window.FoldedControls.Count>0,"A narrow player did not fold its tools into the menu.");
  InterfaceScaling.Apply(1.25);await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
  Require(window.MinWidth>650&&Math.Abs(Line(T("Play or pause"))-Line(T("Full screen (F11)")))<2,"The minimum size or the row did not follow the interface size.");
  var scaledMenu=Menus.Create([MenuSpec.Command(T("Close"),()=>{})]);scaledMenu.IsOpen=true;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
  Require(scaledMenu.LayoutTransform is System.Windows.Media.ScaleTransform{ScaleX:1.25},"A menu did not follow the interface size.");scaledMenu.IsOpen=false;
  InterfaceScaling.Apply(1);Require(window.MinWidth==650,"The minimum size did not return to normal.");
  // The narrowest window: everything stays in reach, the list steps aside without changing the saved choice, and it can still be opened.
  var listSaved=db.GetPreferences().Playback.ShowMarks;if(!window.MarkPanelVisible&&!listSaved){window.ToggleMarkPanel();listSaved=true;}
  window.Width=window.MinWidth;await Idle();
  foreach(var button in Descendants<Button>(window.ControlsSurface).Where(button=>button.IsVisible))
  {
   var point=button.TransformToAncestor(window.ControlsSurface).Transform(new Point());
   Require(point.X>=-1&&point.X+button.ActualWidth<=window.ControlsSurface.ActualWidth+1,"The narrowest player clips a control: "+button.Content);
  }
  Require(Math.Abs(Middle("Play or pause"))<2&&!window.MarkPanelVisible&&db.GetPreferences().Playback.ShowMarks==listSaved,"The narrowest player lost the middle, kept the list or changed the saved choice.");
  window.ToggleMarkPanel();await Idle();Require(window.MarkPanelVisible&&db.GetPreferences().Playback.ShowMarks==listSaved,"The list could not be opened in a narrow window.");window.ToggleMarkPanel();
  window.Width=1100;await Idle();Require(window.MarkPanelVisible==listSaved&&!window.ControlsFolded&&!window.MoreMenuShown&&Math.Abs(Middle("Play or pause"))<2,"A wider window did not bring everything back.");
  File.WriteAllText(Path.Combine(root,"ab-result.txt"),"PASS: range to scene and loop with native state, invalid end retention, repeat independence, discard, messages, fullscreen list/menu/refresh/overlay, range row and normal-state restoration.");
 }
}
