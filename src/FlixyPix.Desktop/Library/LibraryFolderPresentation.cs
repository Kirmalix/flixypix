using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
namespace FlixyPix.Desktop;
public partial class MainWindow
{
 private bool _restoringFolderViewport;
 private double _folderDesiredHeight=260;
 private double _folderSavedX,_folderSavedY;
 private ContextMenu? _sidebarToolsMenu;
 private readonly DispatcherTimer _folderScrollSave=new(){Interval=TimeSpan.FromMilliseconds(300)};
 private void InitializeFolderPresentation()
 {
  var state=_repository.GetPreferences().Sidebar;
  _folderSavedX=state.FolderHorizontalOffset;_folderSavedY=state.FolderVerticalOffset;
  _sidebarSections["folders"].Expanded+=(_,_)=>RestoreFolderViewport();
  _folderDesiredHeight=double.IsFinite(state.FolderHeight)?Math.Clamp(state.FolderHeight,140,600):260;
  void Fit() => FolderViewport.Height=Math.Min(_folderDesiredHeight,Math.Max(140,Sidebar.ActualHeight*.6));
  Sidebar.SizeChanged+=(_,_)=>Fit();Fit();
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
  SaveSidebar(s=>s with {FolderHeight=_folderDesiredHeight,FolderHorizontalOffset=_folderSavedX,FolderVerticalOffset=_folderSavedY});
 }
 private void FolderViewportChanged(object sender,ScrollChangedEventArgs e)
 {
  if(_restoringFolderViewport||!FolderViewport.IsVisible||e.HorizontalChange==0&&e.VerticalChange==0)return;
  _folderSavedX=FolderViewport.HorizontalOffset;_folderSavedY=FolderViewport.VerticalOffset;
  _folderScrollSave.Stop();_folderScrollSave.Start();
 }
 private void FolderHeightDragging(object sender,DragDeltaEventArgs e)
 { _folderDesiredHeight=Math.Clamp(_folderDesiredHeight+e.VerticalChange,140,600);FolderViewport.Height=Math.Min(_folderDesiredHeight,Math.Max(140,Sidebar.ActualHeight*.6)); }
 private void FolderHeightCommitted(object sender,DragCompletedEventArgs e)=>SaveFolderViewportState();
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
  var target=FolderViewport;
  if(!horizontal&&(steps>0&&target.VerticalOffset<=.5||steps<0&&target.VerticalOffset>=target.ScrollableHeight-.5))target=Visuals<ScrollViewer>(Sidebar).First();
  var lines=SystemParameters.WheelScrollLines;
  var distance=lines<0?(horizontal?target.ViewportWidth:target.ViewportHeight):lines*28;
  if(horizontal)target.ScrollToHorizontalOffset(target.HorizontalOffset-steps*distance);
  else target.ScrollToVerticalOffset(target.VerticalOffset-steps*distance);
 }
 private void SidebarTagTools(object sender,RoutedEventArgs e)=>_sidebarToolsMenu=MenuBelow(sender,
  (T("Sort tags by frequency or name"),()=>TagSortClicked(sender,e)),
  (T("Open directory"),()=>DirectoryClicked(sender,e)));
}
