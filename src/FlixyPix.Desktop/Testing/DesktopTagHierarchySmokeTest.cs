using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private async Task RunTagHierarchySmokeAsync(string directory)
 {
  static void Require(bool condition,string text){if(!condition)throw new InvalidOperationException(text);}
  static bool Same(TagKey a,TagKey b)=>TagParentsWindow.KeyComparer.Instance.Equals(a,b);
  static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  async Task Idle()=>await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
  foreach(var encrypted in new[]{false,true})
  {
   var root=Path.Combine(directory,encrypted?"hierarchy-ui-encrypted":"hierarchy-ui-plain");Directory.CreateDirectory(root);var path=Path.Combine(root,"catalog.db");using(var empty=new CatalogRepository(path)){}
   if(encrypted)CatalogEncryption.EnableEncryption(path,"synthetic-hierarchy-ui-password");using var key=encrypted?CatalogEncryption.Unlock(path,"synthetic-hierarchy-ui-password"):null;using var db=new CatalogRepository(path,key);
   var a=new TagKey("Genre","Root A");var b=new TagKey("Genre","Root B");var child=new TagKey("Zed","Same");var other=new TagKey("Alpha","Same");var leaf=new TagKey("Zed","Leaf");var concurrent=new TagKey("Genre","Concurrent");
   foreach(var tag in new[]{a,b,child,other,leaf,concurrent})db.UpsertVocabularyTag(new(tag.Value,tag.Category));
   db.AddTagParent(b,a);db.AddTagParent(child,a);db.AddTagParent(leaf,child);
   db.Upsert(new(){Path=Path.Combine(root,"child.mp4"),Kind=MediaKind.Video,TagCategories=new(){["Zed"]=["Same"],["Alpha"]=["Same"]}});
   db.Upsert(new(){Path=Path.Combine(root,"leaf.mp4"),Kind=MediaKind.Video,TagCategories=new(){["Zed"]=["Leaf"]}});
   db.Upsert(new(){Path=Path.Combine(root,"other.mp4"),Kind=MediaKind.Video,TagCategories=new(){["Alpha"]=["Same"]}});
   var host=new Window{Owner=this,Width=1120,Height=820,ShowInTaskbar=false};var browser=new DirectoryBrowser(host,db);host.Content=browser;host.Show();browser.NavigateHierarchyTag(child);await Idle();
   Button Action(string label)=>Descendants<Button>(browser).Single(button=>Equals(button.Content,T(label)));
   async Task Edit(Func<TagParentsWindow,Task> respond)
   {
    Action("Edit parents").BringIntoView();await Idle();
    Exception? failure=null;
    var response=Dispatcher.InvokeAsync<Task>(async()=>
    {
     var window=Application.Current.Windows.OfType<TagParentsWindow>().Single(w=>w.Owner==host&&w.IsVisible);
     try{await respond(window);}catch(Exception error){failure=error;window.Close();}
    },DispatcherPriority.ApplicationIdle);
    Click(Action("Edit parents"));await response.Task.Unwrap();if(failure!=null)throw failure;
   }
   CheckBox Box(TagParentsWindow window,TagKey wanted)=>Descendants<CheckBox>((FrameworkElement)window.Content).Single(box=>box.DataContext is TagParentsWindow.Choice choice&&Same(choice.Key,wanted));
   try
   {
    await Edit(async window=>
    {
     await Idle();Require(!Box(window,child).IsEnabled&&!Box(window,leaf).IsEnabled&&Box(window,other).IsEnabled,"Parent picker confused identity or allowed descendant.");
     window.Search.Text="Root B";await Idle();Box(window,b).IsChecked=true;window.Search.Text="";await Idle();Box(window,other).IsChecked=true;
     Require(Box(window,a).IsChecked==true&&Box(window,b).IsChecked==true,"Parent selection lost on search.");Capture((FrameworkElement)window.Content,Path.Combine(root,"selected-parents.png"));Click(window.ApplyButton);await window.ApplyTask;if(window.IsVisible)throw new InvalidOperationException(window.FailureText);
    });
    Require(new HashSet<TagKey>(db.GetTagParents(child),TagParentsWindow.KeyComparer.Instance).SetEquals([a,b,other]),"Parent modal did not save chosen parents.");Require(browser.Current is {Category:"Zed",Tag:"Same"}&&browser.CanUndo,"Parent apply changed selected key or omitted undo.");
    var before=db.GetTagHierarchy();await Edit(async window=>{await Idle();Box(window,a).IsChecked=false;Click(Descendants<Button>((FrameworkElement)window.Content).Single(button=>Equals(button.Content,T("Cancel"))));});Require(before.SequenceEqual(db.GetTagHierarchy()),"Cancelled parent dialog wrote changes.");
    var category=Descendants<ComboBox>(browser).Single(box=>System.Windows.Automation.AutomationProperties.GetName(box)==T("Category"));category.Text="Moved";Action("Save").BringIntoView();await Idle();Click(Action("Save"));
    Require(browser.Current is {Category:"Moved",Tag:"Same"}&&browser.CanUndo&&db.GetTagParents(new("Moved","Same")).Count==3,"Category move lost parents or visible undo.");Click(Action("Undo"));Require(browser.Current is {Category:"Zed",Tag:"Same"}&&db.GetTagParents(child).Count==3,"Category undo lost exact key or parents.");
    var name=Descendants<TextBox>(browser).Single(box=>System.Windows.Automation.AutomationProperties.GetName(box)==T("Name"));name.Text="Renamed";Action("Save").BringIntoView();await Idle();Click(Action("Save"));
    Require(browser.Current is {Category:"Zed",Tag:"Renamed"}&&browser.CanUndo,"Rename selected same-name other category or omitted undo.");Click(Action("Undo"));Require(browser.Current is {Category:"Zed",Tag:"Same"},"Structural undo lost original exact key.");
    Action("Edit parents").BringIntoView();await Idle();var parentButton=Descendants<Button>(browser).Single(button=>button.Tag is TagKey tag&&Same(tag,other));name.Text="Unsaved";browser.LeaveDecision=()=>MessageBoxResult.Cancel;Click(parentButton);Require(browser.Current?.Category=="Zed"&&browser.HasUnsavedProfile,"Cancelled hierarchy navigation lost draft.");
    browser.LeaveDecision=()=>MessageBoxResult.No;Click(parentButton);Require(browser.Current?.Category=="Alpha"&&browser.Current.Tag=="Same","Hierarchy link selected wrong category.");browser.LeaveDecision=null;
    browser.NavigateHierarchyTag(a);SearchExpression? query=null;browser.ShowHierarchyRequested+=expression=>query=expression;Click(Action("Show including descendants"));Require(query!=null&&db.QueryPage(new(){Expression=query}).TotalCount==2,"Explicit descendants counted twice or included homonym.");
    Require(db.QueryPage(new(){Expression=CatalogRepository.HasCategorizedTag(a)}).TotalCount==0,"Exact tag search expanded implicitly.");
    browser.NavigateHierarchyTag(child);await Edit(async window=>
    {
     await Idle();db.AddTagParent(child,concurrent);Box(window,a).IsChecked=false;Click(window.ApplyButton);await window.ApplyTask;
     Require(window.IsVisible&&Box(window,a).IsChecked==false&&!string.IsNullOrEmpty(window.FailureText)&&db.GetTagParents(child).Count==4,"Stale parent save overwrote changes or lost choices.");window.Close();
    });
    browser.RefreshDirectory();await Idle();Action("Edit parents").BringIntoView();await Idle();Capture((FrameworkElement)host.Content,Path.Combine(root,"hierarchy-directory.png"));
    File.WriteAllText(Path.Combine(root,"result.txt"),"PASS: typed parent picker/search/apply/cancel/stale retention, visible structural undo, exact-key navigation/draft guard and explicit deduplicated descendant query.");
   }
   finally{browser.LeaveDecision=()=>MessageBoxResult.No;host.Close();}
  }
 }
 private static async Task RunTagHierarchyHostProbeAsync(string parentRoot,CatalogKey? key,ColorScheme scheme)
 {
  var root=Path.Combine(parentRoot,"hierarchy-host-"+scheme);Directory.CreateDirectory(root);var path=Path.Combine(root,"catalog.db");
  using(var empty=new CatalogRepository(path)){}
  if(key!=null)CatalogEncryption.EnableEncryption(path,MetadataCrashProbe.Password);
  using var ownKey=key==null?null:CatalogEncryption.Unlock(path,MetadataCrashProbe.Password);using var db=new CatalogRepository(path,ownKey);
  var child=new TagKey("Zed","Same");var studio=new TagKey("Alpha","Same");var parent=new TagKey("Genre","Root A");var leaf=new TagKey("Zed","Leaf");
  foreach(var tag in new[]{child,studio,parent,leaf})db.UpsertVocabularyTag(new(tag.Value,tag.Category));
  db.AddTagParent(child,parent);db.AddTagParent(child,studio);db.AddTagParent(leaf,child);
  foreach(var tag in new[]{child,leaf,studio})db.Upsert(new(){Path=Path.Combine(root,tag.Category+tag.Value+".mp4"),Kind=MediaKind.Video,TagCategories=new(){[tag.Category]=[tag.Value]}});
  await RunTagHierarchyHostSmokeAsync(root,db,ownKey,child,studio,parent);
  File.WriteAllText(Path.Combine(parentRoot,"hierarchy-host-result.txt"),File.ReadAllText(Path.Combine(root,"host-result.txt")));
 }
 private static async Task RunTagHierarchyHostSmokeAsync(string root,CatalogRepository db,CatalogKey? key,TagKey child,TagKey studio,TagKey parent)
 {
  static void Require(bool condition,string text){if(!condition)throw new InvalidOperationException(text);}
  static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  db.SetTagRole(child.Category,TagRole.Performer);db.SetTagRole(studio.Category,TagRole.Studio);
  var preferences=db.GetPreferences();db.SavePreferences(preferences with {General=preferences.General with {RestoreLastView=true,RestoreScope=RestoreScope.All},Home=preferences.Home with {StartOnHome=false}});
  db.SaveViewState(new(){View="performers",Directory=new(child)});
  var window=new MainWindow(new DesktopSettings{DataDirectory=root,AutoGeneratePreviews=false},persistPreferences:false,unlockedKey:key);
  var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);window.Closed+=(_,_)=>closed.TrySetResult();
  async Task Idle()=>await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
  Button Link(TagKey target)=>Descendants<Button>(window._roleBrowser!).Single(button=>button.Tag is TagKey tag&&TagParentsWindow.KeyComparer.Instance.Equals(tag,target));
  try
  {
   window.Show();await window.Startup;await window._lastRefresh;await Idle();
   Require(window._view=="performers"&&window._roleBrowser?.Current is {Category:"Zed",Tag:"Same"},"Main role hierarchy fixture did not restore exact entry.");
   var name=Descendants<TextBox>(window._roleBrowser!).Single(box=>System.Windows.Automation.AutomationProperties.GetName(box)==T("Name"));name.Text="Unsaved role name";
   window._roleBrowser!.LeaveDecision=()=>MessageBoxResult.Cancel;Click(Link(studio));Require(window._view=="performers"&&window._roleBrowser.HasUnsavedProfile,"Cross-role cancellation lost the profile draft.");
   window._roleBrowser.LeaveDecision=()=>MessageBoxResult.No;Click(Link(studio));await window._lastRefresh;await Idle();
   Require(window._view=="studios"&&window._roleBrowser?.Current is {Category:"Alpha",Tag:"Same"},"Cross-role parent link did not select exact studio.");
   Click(Link(child));await window._lastRefresh;await Idle();Require(window._view=="performers"&&window._roleBrowser?.Current is {Category:"Zed",Tag:"Same"},"Studio child link did not return to performer.");
   Link(parent).BringIntoView();await Idle();Exception? failure=null;
   var response=window.Dispatcher.InvokeAsync<Task>(async()=>
   {
    var dialog=Application.Current.Windows.OfType<DirectoryWindow>().Single(w=>w.Owner==window&&w.IsVisible);
    try
    {
     await Idle();Require(dialog.Browser.Current is {Category:"Genre",Tag:"Root A"},"Role-to-tag link opened wrong directory entry.");
     Capture((FrameworkElement)dialog.Content,Path.Combine(root,"hierarchy-tag-host.png"));
     var show=Descendants<Button>(dialog.Browser).Single(button=>Equals(button.Content,T("Show including descendants")));show.BringIntoView();await Idle();Click(show);
    }
    catch(Exception error){failure=error;dialog.Close();}
   },DispatcherPriority.ApplicationIdle);
   Click(Link(parent));await response.Task.Unwrap();if(failure!=null)throw failure;await window._lastRefresh;await Idle();
   Require(window._view=="all"&&window._repository.QueryPage(new(){Expression=window.CaptureViewState().Expression}).TotalCount==2,"Directory host did not forward explicit descendant query.");
   File.WriteAllText(Path.Combine(root,"host-result.txt"),"PASS: main performer/studio typed links, dirty cancel/discard, generic tag host and explicit descendant query bridge.");
  }
  finally{if(window._roleBrowser!=null)window._roleBrowser.LeaveDecision=()=>MessageBoxResult.No;window.Close();await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));}
 }
}
