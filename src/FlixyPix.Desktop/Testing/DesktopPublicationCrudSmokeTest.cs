using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private async Task RunPublicationCrudSmokeAsync(string root)
 {
  static void Require(bool condition,string text){if(!condition)throw new InvalidOperationException(text);}
  static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  void Dialog(Action open,Action<Window> answer)
  {
   Exception? failure=null;
   var response=Dispatcher.InvokeAsync(()=>
   {
    var window=Application.Current.Windows.OfType<Window>().LastOrDefault(w=>w.Owner==this&&w.IsVisible);
    try{if(window==null)throw new InvalidOperationException("Expected owned group dialog.");answer(window);}
    catch(Exception error){failure=error;window?.Close();}
   },DispatcherPriority.ApplicationIdle);
   open();if(response.Status!=DispatcherOperationStatus.Completed){response.Abort();throw new InvalidOperationException("Group dialog did not run.");}
   if(failure!=null)throw failure;
  }
  void Fields(string action,string name,bool apply)
  {
   Dialog(()=>Click(_publicationActions[action]),window=>
   {
    Require(window.Title==T("Publication groups"),"Wrong group editor.");var fields=Descendants<TextBox>((FrameworkElement)window.Content).ToArray();Require(fields.Length==2,"Group fields missing.");
    fields[0].Text=name;fields[1].Text="Description "+name;
    Click(Descendants<Button>((FrameworkElement)window.Content).Single(b=>Equals(b.Content,T(apply?"Apply":"Cancel"))));
   });
  }
  void Delete(bool confirm)
  {
   Dialog(()=>Click(_publicationActions["Delete"]),window=>
   {
    Require(window is MessageWindow,"Group deletion did not ask.");var message=(MessageWindow)window;
    Require(Equals(message.Choices.Single(b=>b.IsDefault).Content,T("Cancel")),"Group deletion default is not to cancel.");Click(message.Choices.Single(b=>Equals(b.Content,T(confirm?"Delete":"Cancel"))));
   });
  }
  async Task AddSet()
  {
   Exception? failure=null;
   var response=Dispatcher.InvokeAsync<Task>(async()=>
   {
    var picker=Application.Current.Windows.OfType<PublicationGroupMembersWindow>().Single(w=>w.Owner==this&&w.IsVisible);
    try
    {
     await picker.LoadTask;picker.Kind.SelectedIndex=1;await picker.LoadTask;
     picker.Rows.Items.Cast<CheckBox>().First(b=>b.IsEnabled).IsChecked=true;Click(picker.ApplyButton);await picker.ApplyTask;
     if(picker.IsVisible)throw new InvalidOperationException(picker.FailureText);
    }
    catch(Exception error){failure=error;picker.Close();}
   },DispatcherPriority.ApplicationIdle);
   Click(_publicationActions["Add"]);await response.Task.Unwrap();if(failure!=null)throw failure;
  }
  var before=CaptureViewState();var oldWidth=Width;Guid? created=null;
  try
  {
   ApplyViewState(new(){View="publication-groups"});await _lastRefresh;var count=_repository.GetPublicationGroups().Count;
   Fields("New entry","Cancelled new",false);Require(_repository.GetPublicationGroups().Count==count,"Cancelled group creation wrote.");
   Fields("New entry","ZZZ CRUD publication",true);created=SelectedPublicationGroup?.Id;var id=created??throw new InvalidOperationException("Group creation failed.");
   Fields("Edit","Cancelled rename",false);Require(_repository.GetPublicationGroup(id)!.Name=="ZZZ CRUD publication","Cancelled group edit wrote.");
   Fields("Edit","ZZZ CRUD renamed",true);Require(_repository.GetPublicationGroup(id) is {Name:"ZZZ CRUD renamed",Description:"Description ZZZ CRUD renamed"},"Actual group edit did not save.");
   await AddSet();Require(_repository.GetPublicationGroup(id)!.Members.Count==1,"Main group Add did not use picker selection.");
   var setId=_repository.GetPublicationGroup(id)!.Members[0].TargetId;var set=_repository.GetImageSet(setId)!;
   var imagePath=_repository.GetMedia(set.MediaIds[0])!.Path;var originalBytes=File.ReadAllBytes(imagePath);
   Click(_publicationActions["Remove"]);Require(_repository.GetPublicationGroup(id)!.Members.Count==0&&!_publicationActions["Open"].IsEnabled,"Actual member removal did not empty group.");
   var video=_repository.QueryPage(new(){Kind=MediaKind.Video,Limit=1}).Items.First();
   var menu=Menus.Create(MediaMenu([video]),(_,error)=>throw error);
   menu.Items.OfType<MenuItem>().Single(m=>Equals(m.Header,T("Publication groups"))).Items.OfType<MenuItem>().Single(m=>Equals(m.Header,"ZZZ CRUD renamed")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
   RefreshPublicationGroups();await AddSet();_publicationMembers!.SelectedIndex=1;Click(_publicationActions["Move up"]);
   Require(_repository.GetPublicationGroup(id)!.Members[0].Kind==PublicationGroupMemberKind.ImageSet,"Reorder of actual picker members failed.");
   Width=760;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateLayout();_toasts.Children.Clear();
   foreach(var button in _publicationActions.Values)
   {var point=button.TransformToAncestor(_publicationPage!).Transform(new Point());Require(point.X>=-1&&point.Y>=-1&&point.X+button.ActualWidth<=_publicationPage!.ActualWidth+1&&point.Y+button.ActualHeight<=_publicationPage.ActualHeight+1,"Publication action clipped at compact width.");}
   Capture(this,Path.Combine(root,"publication-crud-compact.png"));
   Delete(false);Require(_repository.GetPublicationGroup(id)!=null,"Declined group deletion wrote.");Delete(true);Require(_repository.GetPublicationGroup(id)==null,"Confirmed group deletion failed.");
   Require(_repository.GetMedia(video.Id)!=null&&_repository.GetImageSet(setId)!=null&&File.ReadAllBytes(imagePath).SequenceEqual(originalBytes),"Group deletion changed target or image bytes.");
   File.WriteAllText(Path.Combine(root,"publication-crud-result.txt"),"PASS: actual create/edit/cancel/Add picker/remove/reorder/defaultNo/delete, compact buttons and preserved targets/files.");
  }
  finally{Width=oldWidth;if(created is {} id)_repository.DeletePublicationGroup(id);ApplyViewState(before);await _lastRefresh;}
 }
}
