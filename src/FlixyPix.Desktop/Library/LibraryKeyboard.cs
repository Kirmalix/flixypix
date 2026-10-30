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
 private readonly Dictionary<FrameworkElement,IInputElement> _regionFocus=[];
 private int? _keyboardIndex,_keyboardAnchor;
 private readonly SemaphoreSlim _keyboardNavigationGate=new(1);
 private FrameworkElement[] KeyboardRegions()=>new FrameworkElement[]{HeaderBar,Sidebar.IsVisible?Sidebar:SidebarRail,_view=="home"?_home:MediaList,DetailsPane.IsVisible?DetailsPane:DetailsRail}.Where(r=>r!=null&&r.IsVisible).ToArray();
 private void MoveKeyboardRegion(bool backwards)
 {
  var regions=KeyboardRegions();if(regions.Length==0)return;
  var index=Array.FindIndex(regions,r=>r.IsKeyboardFocusWithin);
  if(index>=0&&Keyboard.FocusedElement is {} focused)_regionFocus[regions[index]]=focused;
  var region=regions[(index+(backwards?-1:1)+regions.Length)%regions.Length];
  if(_regionFocus.TryGetValue(region,out var remembered)&&remembered is UIElement {IsVisible:true,IsEnabled:true} element)element.Focus();
  else if(region==MediaList) {MediaList.Focus();if(MediaList.SelectedItem is {} item)(MediaList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem)?.Focus();}
  else region.MoveFocus(new(FocusNavigationDirection.First));
 }
 internal static bool TextEntry(object? source)
 {
  for(var element=source as DependencyObject;element!=null;element=element is Visual?VisualTreeHelper.GetParent(element):LogicalTreeHelper.GetParent(element))
   if(element is TextBoxBase or PasswordBox or ComboBox)return true;
  return false;
 }
 private static bool ContextKey(KeyEventArgs e)=>e.Key==Key.Apps||((e.Key==Key.F10||e.Key==Key.System&&e.SystemKey==Key.F10)&&Keyboard.Modifiers==ModifierKeys.Shift);
 private async Task<bool> HandleLibraryKeyboardAsync(KeyEventArgs e)
 {
  var key=e.Key==Key.System?e.SystemKey:e.Key;
  if(key==Key.Escape)
  {
   if(FilterFlyout.IsOpen){FilterFlyout.IsOpen=false;FilterButton.Focus();return true;}
   if(ViewPopup.IsOpen){ViewPopup.IsOpen=false;ViewButton.Focus();return true;}
  }
  if(key==Key.F6&&Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift){MoveKeyboardRegion(Keyboard.Modifiers==ModifierKeys.Shift);return true;}
  if(AppActions().FirstOrDefault(a=>a.Matches(e)) is {} action) {if((key==Key.Home||key==Key.I)&&TextEntry(e.OriginalSource))return false;action.Run();return true;}
  if(TextEntry(e.OriginalSource))return false;
  if(Tags.IsKeyboardFocusWithin&&Tags.SelectedItem is TagRow tag)
  {
   if(ContextKey(e)){OpenTagMenu(tag,Tags);return true;}
   if(key==Key.Enter&&Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Control){ToggleTagFilter(tag.Tag,Keyboard.Modifiers==ModifierKeys.Control);return true;}
  }
  if(CollectionTree.IsKeyboardFocusWithin&&ContextKey(e)&&CollectionTree.SelectedItem is CollectionNode node){Menus.Open(Keyboard.FocusedElement as UIElement,CollectionNodeMenu(node),MenuFailed);return true;}
  if((Folders.IsKeyboardFocusWithin||CategorizedTags.IsKeyboardFocusWithin)&&key==Key.Enter)return true;
  if(!MediaList.IsKeyboardFocusWithin||_total==0)return false;
  if(Keyboard.FocusedElement is ButtonBase or Slider)return false;
  var modifiers=Keyboard.Modifiers;
  if(key!=Key.A&&key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Space))return false;
  if(key==Key.A&&modifiers!=ModifierKeys.Control||key==Key.Space&&modifiers!=ModifierKeys.Control)return false;
  if(modifiers is not (ModifierKeys.None or ModifierKeys.Control or ModifierKeys.Shift or (ModifierKeys.Control|ModifierKeys.Shift)))return false;
  e.Handled=true;await _keyboardNavigationGate.WaitAsync();
  try {
  if(_closing||_total==0)return true;
  var navigationVersion=_refreshVersion;
  if(key==Key.A&&modifiers==ModifierKeys.Control&&_window is {} selectWindow)
  {
   e.Handled=true;var all=new List<MediaItem>();
   for(int offset=0;offset<_total;offset+=WindowSize) {var query=selectWindow.Query with {Offset=offset,Limit=WindowSize};var page=await Task.Run(()=>selectWindow.Playlist==null?_repository.QueryPage(query):_repository.QueryPlaylistPage(selectWindow.Playlist,query,selectWindow.CollectionOrder).Page);all.AddRange(page.Items);}
   if(navigationVersion!=_refreshVersion)return true;
   _refreshing=true;try {MediaList.SelectAll();var loaded=MediaList.Items.Cast<MediaCard>().Select(c=>c.Item.Id).ToHashSet();_offWindowSelection=all.Where(i=>!loaded.Contains(i.Id)).ToList();}finally{_refreshing=false;}ShowDetails();return true;
  }
  if(key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Space))return false;
  if(modifiers is not (ModifierKeys.None or ModifierKeys.Control or ModifierKeys.Shift or (ModifierKeys.Control|ModifierKeys.Shift)))return false;
  var focused=Keyboard.FocusedElement as ListBoxItem;
  var current=focused?.DataContext is MediaCard focusedCard?MediaList.Items.IndexOf(focusedCard)+_windowStart:_keyboardIndex??Math.Max(0,MediaList.SelectedIndex+_windowStart);
  if(key==Key.Space)
  {
   if(modifiers!=ModifierKeys.Control)return false;
   if(MediaList.ItemContainerGenerator.ContainerFromIndex(current-_windowStart) is ListBoxItem container)container.IsSelected=!container.IsSelected;
   return true;
  }
  var columns=Tiles?.Columns??1;
  var target=Math.Clamp(key switch {Key.Home=>0,Key.End=>_total-1,Key.Left=>current-1,Key.Right=>current+1,Key.Up=>current-columns,Key.Down=>current+columns,Key.PageUp=>current-PageSize,Key.PageDown=>current+PageSize,_=>current},0,_total-1);
  if(_keyboardAnchor==null)_keyboardAnchor=current;
  if(!modifiers.HasFlag(ModifierKeys.Shift))_keyboardAnchor=target;
  e.Handled=true;
  var selected=SelectedMedia.ToList();
  await ScrollToAsync(target);if(navigationVersion!=_refreshVersion)return true;MediaList.UpdateLayout();
  if(modifiers.HasFlag(ModifierKeys.Shift)&&_window is {} window)
  {
   int first=Math.Min(_keyboardAnchor.Value,target),last=Math.Max(_keyboardAnchor.Value,target);var range=new List<MediaItem>();
   for(int offset=first;offset<=last;offset+=WindowSize)
   {
    var query=window.Query with {Offset=offset,Limit=Math.Min(WindowSize,last-offset+1)};
    var page=await Task.Run(()=>window.Playlist==null?_repository.QueryPage(query):_repository.QueryPlaylistPage(window.Playlist,query,window.CollectionOrder).Page);range.AddRange(page.Items);
   }
   selected=modifiers.HasFlag(ModifierKeys.Control)?selected.Concat(range).DistinctBy(m=>m.Id).ToList():range;
  }
  else if(!modifiers.HasFlag(ModifierKeys.Control))selected=[((MediaCard)MediaList.Items[target-_windowStart]).Item];
  if(navigationVersion!=_refreshVersion)return true;
  var targetContainer=MediaList.ItemContainerGenerator.ContainerFromIndex(target-_windowStart) as ListBoxItem;
  _refreshing=true;
  try {
   Keyboard.Focus(targetContainer??(IInputElement)MediaList);
   var ids=selected.Select(m=>m.Id).ToHashSet();MediaList.SelectedItems.Clear();foreach(var card in MediaList.Items.Cast<MediaCard>().Where(c=>ids.Contains(c.Item.Id)))MediaList.SelectedItems.Add(card);var loaded=MediaList.Items.Cast<MediaCard>().Select(c=>c.Item.Id).ToHashSet();_offWindowSelection=selected.Where(m=>!loaded.Contains(m.Id)).ToList();
  }
  finally {_refreshing=false;}
  ShowDetails();
  _keyboardIndex=target;return true;
  }finally {_keyboardNavigationGate.Release();}
 }
 private void InitializeKeyboardAccess()
 {
  KeyboardNavigation.SetTabNavigation(MediaList,KeyboardNavigationMode.Once);KeyboardNavigation.SetTabNavigation(Tags,KeyboardNavigationMode.Once);
  FilterFlyout.Opened+=(_,_)=>{KeyboardNavigation.SetTabNavigation(FilterFlyoutFrame,KeyboardNavigationMode.Cycle);FilterFlyoutFrame.MoveFocus(new(FocusNavigationDirection.First));};
  FilterFlyout.Closed+=(_,_)=>FilterButton.Focus();
  ViewPopup.Opened+=(_,_)=>{KeyboardNavigation.SetTabNavigation(ViewPopupFrame,KeyboardNavigationMode.Cycle);ViewPopupFrame.MoveFocus(new(FocusNavigationDirection.First));};ViewPopup.Closed+=(_,_)=>ViewButton.Focus();
  MediaList.PreviewMouseDown+=(_,_)=>{_keyboardIndex=null;_keyboardAnchor=null;};
 }
}
