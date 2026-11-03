using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

internal sealed partial class DirectoryBrowser
{
 private sealed record HierarchyLink(TagKey Key,string Label);
 private readonly ListBox hierarchyParents=new(){MaxHeight=120},hierarchyChildren=new(){MaxHeight=160};
 private readonly TextBlock hierarchyParentLabel=new(),hierarchyChildLabel=new();
 private FrameworkElement HierarchySection()
 {
  var section=new StackPanel{Margin=new Thickness(0,0,0,12)};
  section.Children.Add(new TextBlock{Text=T("Tag hierarchy"),FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,5)});
  var edit=new Button{Content=T("Edit parents")};edit.Click+=(_,_)=>EditHierarchyParents();
  var show=new Button{Content=T("Show including descendants")};show.Click+=(_,_)=>RequestHierarchyMedia();
  var actions=new WrapPanel{Margin=new Thickness(0,0,0,8)};foreach(var action in new[]{edit,show}){action.Margin=new Thickness(0,0,8,6);actions.Children.Add(action);}section.Children.Add(actions);
  foreach(var list in new[]{hierarchyParents,hierarchyChildren})
  {
   ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);
   var button=new FrameworkElementFactory(typeof(Button));button.SetBinding(ToolTipProperty,new Binding(nameof(HierarchyLink.Label)));button.SetBinding(TagProperty,new Binding(nameof(HierarchyLink.Key)));button.SetValue(MarginProperty,new Thickness(0,2,0,2));button.SetValue(HorizontalAlignmentProperty,HorizontalAlignment.Stretch);
   var text=new FrameworkElementFactory(typeof(TextBlock));text.SetBinding(TextBlock.TextProperty,new Binding(nameof(HierarchyLink.Label)));text.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);button.AppendChild(text);
   button.AddHandler(Button.ClickEvent,new RoutedEventHandler((sender,_)=>{if(sender is Button{Tag:TagKey key})NavigateHierarchyTag(key);}));list.ItemTemplate=new DataTemplate{VisualTree=button};
  }
  section.Children.Add(hierarchyParentLabel);section.Children.Add(hierarchyParents);section.Children.Add(hierarchyChildLabel);section.Children.Add(hierarchyChildren);return section;
 }
 private void ShowHierarchy(DirectoryEntry? entry)
 {
  var key=entry==null?null:new TagKey(entry.Category,entry.Tag);
  var parents=key==null?[]:repository.GetTagParents(key);var children=key==null?[]:repository.GetTagChildren(key);
  hierarchyParentLabel.Text=T("Parents")+": "+parents.Count;hierarchyChildLabel.Text=T("Children")+": "+children.Count;
  hierarchyParents.ItemsSource=parents.Select(p=>new HierarchyLink(p,TagParentsWindow.Label(p))).ToArray();hierarchyChildren.ItemsSource=children.Select(p=>new HierarchyLink(p,TagParentsWindow.Label(p))).ToArray();
  hierarchyParents.Visibility=parents.Count==0?Visibility.Collapsed:Visibility.Visible;hierarchyChildren.Visibility=children.Count==0?Visibility.Collapsed:Visibility.Visible;
 }
 internal void EditHierarchyParents()
 {
  if(!ConfirmLeave()||Current is not {} entry)return;var key=new TagKey(entry.Category,entry.Tag);Reload(exact:key);var expected=repository.GetTagParents(key);TagChangeUndo? change=null;
  var window=new TagParentsWindow(dialogOwner,key,repository.GetDirectory(),expected,repository.GetTagDescendants(key),async parents=>change=await Task.Run(()=>repository.SetTagParents(key,parents,expected)));
  if(window.ShowDialog()==true){Changed=true;Reload(exact:key);ShowUndo(change,T("Saved"),key);}
 }
 internal void RefreshDirectory()=>Reload();
 internal bool NavigateHierarchyTag(TagKey key)
 {
  if(!ConfirmLeave())return false;
  if(!repository.GetDirectory().Any(e=>TagParentsWindow.KeyComparer.Instance.Equals(new(e.Category,e.Tag),key))){frame.Status.Text=T("The tag is not in the directory.");return false;}
  if(useMainSearch){if(HierarchyTagRequested==null)return false;HierarchyTagRequested.Invoke(key);return true;}
  resetting=true;try{filter=null;search.Text="";}finally{resetting=false;}
  Reload(exact:key);return Current is {} selected&&TagParentsWindow.KeyComparer.Instance.Equals(new(selected.Category,selected.Tag),key);
 }
 internal void RequestHierarchyMedia()
 {
  if(!ConfirmLeave()||Current is not {} entry)return;var keys=repository.GetTagDescendants(new(entry.Category,entry.Tag),true);
  var expression=SearchExpression.Any(keys.Select(key=>key.Category.Length>0?CatalogRepository.HasCategorizedTag(key):SearchExpression.Where(new(){Field=SearchField.Tag,Comparison=SearchComparison.Equals,Text=key.Value})).ToArray());
  ShowHierarchyExpression=expression;ShowMediaKey=null;ShowHierarchyRequested?.Invoke(expression);
 }
}
