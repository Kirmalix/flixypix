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
  Button ActionButton(string label)=>Descendants<Button>((FrameworkElement)window.Content).Single(button=>Equals(System.Windows.Automation.AutomationProperties.GetName(button),T(label)));
  static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  var setA=ActionButton("Set A");var setB=ActionButton("Set B");var reset=ActionButton("Reset A–B");var repeat=window.Repeat;window.Repeat=true;
  Require(window.AbLoop==new AbLoopState()&&!setB.IsEnabled,"Fresh window inherited loop points or allowed B without A.");
  await window.SeekToAsync(1);Click(setA);await window.AbLoopUpdate;Require(window.AbLoop is {Start:1,End:null}&&setB.IsEnabled,"A button did not enter waiting-for-B state.");
  await window.SeekToAsync(2);Click(setB);await window.AbLoopUpdate;Require(window.AbLoop is {Start:1,End:2}&&window.Repeat,"AB changed playlist repeat or failed to activate.");
  Require(await player.GetAbLoopAsync()==window.AbLoop,"UI loop did not reach actual mpv.");
  await window.SeekToAsync(.5);Click(setB);await window.AbLoopUpdate;Require(window.AbLoop is {Start:1,End:2}&&await player.GetAbLoopAsync()==window.AbLoop,"Invalid B replaced a valid loop.");
  var sceneCount=db.GetScenes(mediaId).Count;await window.SeekToAsync(3);Click(ActionButton("Scene start"));await window.SeekToAsync(4);Click(ActionButton("Scene end"));Click(ActionButton("Create scene"));
  Require(db.GetScenes(mediaId).Count==sceneCount+1&&db.GetScenes(mediaId).Any(scene=>scene.StartSeconds==3&&scene.EndSeconds==4)&&window.AbLoop is {Start:1,End:2}&&window.ClipStart==null,"Visible scene actions changed loop points or saved wrong range.");
  var bookmarkCount=db.GetBookmarks(mediaId).Count;Click(ActionButton("Set bookmark"));Require(db.GetBookmarks(mediaId).Count==bookmarkCount+1,"Visible bookmark action did not persist.");
  var rating=db.GetMedia(mediaId)!.Rating;window.Rate(7);Require(db.GetMedia(mediaId)!.Rating==7&&window.Rating==7,"Player rating was not saved.");window.Rate(rating);
  await window.SeekToAsync(5);Click(setA);await window.AbLoopUpdate;Require(window.AbLoop is {Start:5,End:null}&&(await player.GetAbLoopAsync()).End==null,"A beyond B did not clear B.");
  Click(reset);await window.AbLoopUpdate;Require(window.AbLoop==new AbLoopState()&&await player.GetAbLoopAsync()==new AbLoopState()&&window.Repeat,"Reset did not clear native points or changed repeat.");window.Repeat=repeat;
  await window.SeekToAsync(.25);Click(setA);await window.AbLoopUpdate;await window.SeekToAsync(.75);Click(setB);await window.AbLoopUpdate;
  Require(window.AbLoopSummary.Contains("0:00.250")&&window.AbLoopSummary.Contains("0:00.750"),"Sub-second loop boundaries are not visible precisely.");Click(reset);await window.AbLoopUpdate;
  var normalWanted=db.GetPreferences().Playback.ShowMarks;window.ToggleFullscreen();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
  Require(!window.MarkPanelVisible,"Fullscreen unexpectedly opened the list before an explicit request.");
  var menu=window.ShowPlayerMenu();var marks=menu.Items.OfType<MenuItem>().Single(item=>Equals(item.Header,T("Jump marks")));Require(marks.IsEnabled,"Fullscreen disabled mark navigation.");marks.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));menu.IsOpen=false;
  Require(window.MarkPanelVisible&&db.GetPreferences().Playback.ShowMarks==normalWanted,"Fullscreen list did not open or changed saved normal visibility.");window.RefreshMarks();Require(window.MarkPanelVisible,"Refresh hid explicitly opened fullscreen marks.");
  window.SimulateRest();Require(window.OverlayShown,"Controls hid while fullscreen marks were open.");
  Require(window.ControlLabels.Contains(T("Set A"))&&window.ControlLabels.Contains(T("Scene start")),"Floating controls omitted loop/scene actions.");
  await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Capture((FrameworkElement)window.Content,Path.Combine(root,"fullscreen-marks.png"));
  Capture(window.ControlsSurface,Path.Combine(root,"fullscreen-controls.png"));
  window.ToggleMarkPanel();Require(!window.MarkPanelVisible,"Fullscreen list did not close.");window.ToggleFullscreen();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Require(window.MarkPanelVisible==normalWanted,"Leaving fullscreen lost normal panel preference.");
  window.Width=760;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
  foreach(var button in Descendants<Button>(window.ControlsSurface).Where(button=>button.IsVisible))
  {
   var point=button.TransformToAncestor(window.ControlsSurface).Transform(new Point());
   Require(point.X>=-1&&point.X+button.ActualWidth<=window.ControlsSurface.ActualWidth+1,"Narrow player clips a control: "+button.Content);
  }
  Capture(window.ControlsSurface,Path.Combine(root,"compact-controls.png"));
  // The row stays one line at every size: what does not fit moves into the menu, and a larger interface keeps its room.
  double Line(string name)=>ActionButton(name).TransformToAncestor(window.ControlsSurface).Transform(new Point(0,ActionButton(name).ActualHeight/2)).Y;
  Require(Math.Abs(Line(T("Play or pause"))-Line(T("Full screen (F11)")))<2,"The control row wrapped.");
  Require(window.ControlsFolded&&window.FoldedControls.Count>0,"A narrow player did not fold its tools into the menu.");
  InterfaceScaling.Apply(1.25);await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
  Require(window.MinWidth>650&&Math.Abs(Line(T("Play or pause"))-Line(T("Full screen (F11)")))<2,"The minimum size or the row did not follow the interface size.");
  var scaledMenu=Menus.Create([MenuSpec.Command(T("Close"),()=>{})]);scaledMenu.IsOpen=true;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
  Require(scaledMenu.LayoutTransform is System.Windows.Media.ScaleTransform{ScaleX:1.25},"A menu did not follow the interface size.");scaledMenu.IsOpen=false;
  InterfaceScaling.Apply(1);Require(window.MinWidth==650,"The minimum size did not return to normal.");
  File.WriteAllText(Path.Combine(root,"ab-result.txt"),"PASS: actual AB controls/native state, invalid B retention, independent scene/bookmark/repeat, reset, fullscreen list/menu/refresh/overlay and normal-state restoration.");
 }
}
