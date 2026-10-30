using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlixyPix.Core;
namespace FlixyPix.Desktop;
public partial class MainWindow
{
 private bool _restoringSidebar;
 private readonly Dictionary<string,Expander> _sidebarSections=[];
 private void SaveSidebar(Func<SidebarPreferences,SidebarPreferences> change)
 {
  if(_restoringSidebar||_initializingBrowsing) return;
  var current=_repository.GetPreferences(); _repository.SavePreferences(current with {Sidebar=change(current.Sidebar)});
 }
 private void InitializeSidebarState()
 {
  MediaList.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,new RoutedEventHandler((sender,e)=> {if(e.OriginalSource is Button {Tag:"quick-collection"} button)TileQuickCollection(button,e);}));
  var saved=_repository.GetPreferences().Sidebar;
  _restoringSidebar=true;
  IncludeFolderChildren.IsChecked=saved.IncludeSubfolders; _tagsByName=saved.TagsByName;TagSortButton.Content=_tagsByName?"Name":T("Frequency"); ShowUnusedTags.IsChecked=saved.ShowUnusedTags;
  _collapsedSets.UnionWith(saved.CollapsedSets);
  foreach(var exp in Logicals<Expander>(Sidebar))
  {
   if(exp.Tag is not string key) continue;
   _sidebarSections[key]=exp;exp.IsExpanded=!saved.CollapsedSections.Contains(key);
   void Changed(object? sender,RoutedEventArgs e) { if(e.Source!=exp) return; SaveSidebar(s=>s with {CollapsedSections=_sidebarSections.Where(p=>!p.Value.IsExpanded).Select(p=>p.Key).ToList()}); }
   exp.Expanded+=Changed; exp.Collapsed+=Changed;
  }
  _restoringSidebar=false;
 }
 private static IEnumerable<T> Logicals<T>(DependencyObject parent) where T:DependencyObject
 {
  foreach(var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()) {if(child is T found)yield return found;foreach(var descendant in Logicals<T>(child))yield return descendant;}
 }
 private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T:DependencyObject
 {
  for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) { var child=VisualTreeHelper.GetChild(parent,i);if(child is T found)yield return found;foreach(var descendant in Visuals<T>(child))yield return descendant; }
 }
 private void RestoreTreeState(TreeView tree,string section)
 {
  var saved=_repository.GetPreferences().Sidebar;
  var open=section=="folders"?saved.ExpandedFolders:saved.ExpandedCategories;
  _restoringSidebar=true;
  foreach(var node in (tree.ItemsSource as IEnumerable<BrowseNode>??[]).SelectMany(n=>n.Descendants().Prepend(n)))
  { node.IsExpanded=node.Path==null&&section=="folders"||open.Contains(node.Path??node.Category??"",StringComparer.OrdinalIgnoreCase);node.ExpandedChanged=()=>SaveTreeState(tree,section); }
  _restoringSidebar=false;
 }
 private void SaveTreeState(TreeView tree,string section)
 {
  var open=(tree.ItemsSource as IEnumerable<BrowseNode>??[]).SelectMany(n=>n.Descendants().Prepend(n)).Where(n=>n.IsExpanded).Select(n=>n.Path??n.Category??"").ToList();
  SaveSidebar(s=>section=="folders"?s with {ExpandedFolders=open}:s with {ExpandedCategories=open});
 }
 private void FolderChildrenChanged(object sender,RoutedEventArgs e) { SaveSidebar(s=>s with {IncludeSubfolders=IncludeFolderChildren.IsChecked==true});_anchor=0;Refresh(); }
 private static bool IsQuickBadge(object source) { for(var node=source as DependencyObject;node!=null;node=VisualTreeHelper.GetParent(node))if(node is Button)return true;return false; }
 private async void QuickTagBadgeClicked(object sender,RoutedEventArgs e)
 { e.Handled=true;if((sender as FrameworkElement)?.DataContext is TagRow row) { var slot=QuickTags.ToList().FindIndex(t=>string.Equals(t,row.Tag,StringComparison.OrdinalIgnoreCase));if(slot>=0)await ToggleQuickTagAsync(slot+1); } }
 private void AssignQuickTag(int slot,string tag)
 {
  var current=_repository.GetPreferences();var slots=(current.QuickTags??[]).Take(9).ToList();while(slots.Count<9)slots.Add(null);
  for(int i=0;i<slots.Count;i++)if(string.Equals(slots[i],tag,StringComparison.OrdinalIgnoreCase))slots[i]=null;
  if(slot>0)slots[slot-1]=tag;_repository.SavePreferences(current with {QuickTags=slots});RenderTagList();UpdateTagEditor(SelectedMedia);
 }
 private void TileQuickCollection(object sender,RoutedEventArgs e) { e.Handled=true;if(CardOf(sender) is {} card)ToggleQuickCollection(card.Item); }
 private void ToggleQuickCollection(MediaItem item)
 {
  var quick=_repository.GetQuickCollection();_repository.ToggleInCollection(quick.Id,[item.Id]);Refresh();
 }
}
