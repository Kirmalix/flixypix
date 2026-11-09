using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
namespace FlixyPix.Desktop;
public partial class MainWindow
{
 private bool _restoringFolderViewport;
 private double _folderSavedX,_folderSavedY;
 private ContextMenu? _sidebarToolsMenu;
 private readonly DispatcherTimer _folderScrollSave=new(){Interval=TimeSpan.FromMilliseconds(300)};
 private void InitializeFolderPresentation()
 {
  var state=_repository.GetPreferences().Sidebar;
  _folderSavedX=state.FolderHorizontalOffset;_folderSavedY=state.FolderVerticalOffset;
  _folderScrollSave.Tick+=(_,_)=>{_folderScrollSave.Stop();SaveFolderViewportState();};
  Folders.Loaded+=(_,_)=>RestoreFolderViewport();
 }
 private void RestoreFolderViewport()
 {
  _restoringFolderViewport=true;
  Dispatcher.BeginInvoke(()=> {
   if(_closing)return;
   var state=_repository.GetPreferences().Sidebar;
   FolderViewport.ScrollToHorizontalOffset(double.IsFinite(state.FolderHorizontalOffset)?Math.Max(0,state.FolderHorizontalOffset):0);
   FolderViewport.ScrollToVerticalOffset(double.IsFinite(state.FolderVerticalOffset)?Math.Max(0,state.FolderVerticalOffset):0);
   Dispatcher.BeginInvoke(()=>_restoringFolderViewport=false,DispatcherPriority.ContextIdle);
  },DispatcherPriority.Loaded);
 }
 private void SaveFolderViewportState()
 {
  _folderScrollSave.Stop();
  if(_restoringFolderViewport)return;
  if(FolderViewport.IsVisible) {_folderSavedX=FolderViewport.HorizontalOffset;_folderSavedY=FolderViewport.VerticalOffset;}
  SaveSidebar(s=>s with {FolderHorizontalOffset=_folderSavedX,FolderVerticalOffset=_folderSavedY});
 }
 private void FolderViewportChanged(object sender,ScrollChangedEventArgs e)
 {
  if(_restoringFolderViewport||!FolderViewport.IsVisible||e.HorizontalChange==0&&e.VerticalChange==0)return;
  _folderSavedX=FolderViewport.HorizontalOffset;_folderSavedY=FolderViewport.VerticalOffset;
  _folderScrollSave.Stop();_folderScrollSave.Start();
 }
 private double _folderWheelRemainder;
 private ModifierKeys _folderWheelModifiers;
 private void FolderViewportWheel(object sender,MouseWheelEventArgs e)
 {
  if(Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift))return;
  e.Handled=true;
  if(_folderWheelModifiers!=Keyboard.Modifiers) {_folderWheelRemainder=0;_folderWheelModifiers=Keyboard.Modifiers;}
  _folderWheelRemainder+=e.Delta;var steps=(int)(_folderWheelRemainder/120);_folderWheelRemainder-=steps*120;
  if(steps==0)return;
  var horizontal=Keyboard.Modifiers==ModifierKeys.Shift;
  var lines=SystemParameters.WheelScrollLines;
  var distance=lines<0?(horizontal?FolderViewport.ViewportWidth:FolderViewport.ViewportHeight):lines*28;
  if(horizontal)FolderViewport.ScrollToHorizontalOffset(FolderViewport.HorizontalOffset-steps*distance);
  else Motion.GlideBy(FolderViewport,-steps*distance);
 }
 private void SidebarTagTools(object sender,RoutedEventArgs e)=>_sidebarToolsMenu=MenuBelow(sender,
  (T("Open directory"),()=>DirectoryClicked(sender,e)));
}
