using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private async Task RunImageSetCrudSmokeAsync(string root)
 {
  static void Require(bool condition,string text){if(!condition)throw new InvalidOperationException(text);}
  async Task Idle()=>await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
  void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  void Dialog(Action open,Action<Window> answer)
  {
   Exception? failure=null;
   var response=Dispatcher.InvokeAsync(()=>
   {
    var dialog=Application.Current.Windows.OfType<Window>().LastOrDefault(w=>w.Owner==this&&w.IsVisible);
    try{if(dialog==null)throw new InvalidOperationException("Expected owned set dialog.");answer(dialog);}
    catch(Exception error){failure=error;dialog?.Close();}
   },DispatcherPriority.ApplicationIdle);
   open();
   if(response.Status!=DispatcherOperationStatus.Completed){response.Abort();throw new InvalidOperationException("Set dialog did not run.");}
   if(failure!=null)throw failure;
  }
  void Fields(string action,string name,string description,bool apply)
  {
   Dialog(()=>Click(_setActions[action]),dialog=>
   {
    Require(dialog.Title==T("Image sets"),"Wrong edit dialog.");
    var fields=Descendants<TextBox>((FrameworkElement)dialog.Content).ToArray();Require(fields.Length==2,"Set editor fields missing.");
    fields[0].Text=name;fields[1].Text=description;
    Click(Descendants<Button>((FrameworkElement)dialog.Content).Single(b=>Equals(b.Content,T(apply?"Apply":"Cancel"))));
   });
  }
  void Delete(bool confirm)
  {
   Dialog(()=>Click(_setActions["Delete"]),dialog=>
   {
    Require(dialog is MessageWindow,"Set delete did not ask confirmation.");var message=(MessageWindow)dialog;
    Require(Equals(message.Choices.Single(b=>b.IsDefault).Content,T("No")),"Set deletion default is not No.");
    Click(message.Choices.Single(b=>Equals(b.Content,T(confirm?"Yes":"No"))));
   });
  }
  var before=CaptureViewState();var oldWidth=Width;var images=new List<MediaItem>();Guid? created=null;
  try
  {
   foreach(var name in new[]{"crud-a.png","crud-b.png"})
   {
    var path=Path.Combine(root,name);using(var image=new ImageMagick.MagickImage(new ImageMagick.MagickColor("cyan"),48,48))image.Write(path,ImageMagick.MagickFormat.Png);
    images.Add(_repository.Upsert(new(){Path=path,Title=name,Kind=MediaKind.Image}));
   }
   ApplyViewState(new(){View="image-sets"});await _lastRefresh;await Idle();
   var count=_repository.GetImageSets().Count;
   Fields("New entry","Cancelled new","No write",false);Require(_repository.GetImageSets().Count==count,"Cancelled creation wrote a set.");
   Fields("New entry","ZZZ CRUD set","Created description",true);created=SelectedImageSet?.Id;
   Require(created!=null&&_repository.GetImageSets().Count==count+1,"Actual new-set action failed.");
   var createdId=created??throw new InvalidOperationException("No created set ID.");
   Fields("Edit","Cancelled rename","No write",false);Require(_repository.GetImageSet(createdId)!.Name=="ZZZ CRUD set","Cancelled edit changed a set.");
   Fields("Edit","ZZZ CRUD renamed","Edited description",true);Require(_repository.GetImageSet(createdId) is {Name:"ZZZ CRUD renamed",Description:"Edited description"},"Actual edit action failed.");
   var menu=Menus.Create(MediaMenu(images),(_,error)=>throw error);
   var submenu=menu.Items.OfType<MenuItem>().Single(m=>Equals(m.Header,T("Image sets")));
   submenu.Items.OfType<MenuItem>().Single(m=>Equals(m.Header,"ZZZ CRUD renamed")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
   RefreshImageSets();await _setCoverTask;
   Require(SelectedImageSet!.MediaIds.SequenceEqual(images.Select(i=>i.Id)),"Actual media-menu add did not preserve order.");
   _setMembers!.SelectedIndex=1;Click(_setActions["Set as cover"]);await _setCoverTask;
   Click(_setActions["Remove"]);await _setCoverTask;
   Require(_repository.GetImageSet(createdId) is {CoverId:null} set&&set.MediaIds.SequenceEqual([images[0].Id]),"Actual member removal left membership or cover.");
   ApplyViewState(new(){View="all"});await _lastRefresh;
   ApplyViewState(new(){View="image-sets",ImageSetId=created});await _lastRefresh;
   await WalkHistoryAsync(-1);Require(!IsImageSetView,"History back did not leave sets.");
   await WalkHistoryAsync(1);Require(IsImageSetView&&SelectedImageSet?.Id==created,"History forward lost selected set.");
   SearchBox.Text="#literal";_searchTimer.Stop();
   SearchKeyDown(SearchBox,new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(SearchBox)!,0,Key.Enter){RoutedEvent=Keyboard.KeyDownEvent});
   await RefreshAsync();Require(IsImageSetView&&SearchBox.Text=="#literal"&&_requiredTags.Count==0,"Set search shorthand changed media filters.");
   ApplyViewState(new(){View="image-sets",ImageSetId=created});await _lastRefresh;await _setCoverTask;
   Require(KeyboardRegions().Contains(_setPage!),"Image-set page missing from keyboard regions.");
   Width=760;await Idle();UpdateLayout();
   foreach(var button in _setActions.Values)
   {
    var point=button.TransformToAncestor(_setPage!).Transform(new Point());
    Require(point.X>=-1&&point.Y>=-1&&point.X+button.ActualWidth<=_setPage!.ActualWidth+1&&point.Y+button.ActualHeight<=_setPage.ActualHeight+1,"Set action clipped at compact width.");
   }
   Capture(this,Path.Combine(root,"image-set-crud-compact.png"));
   Delete(false);Require(_repository.GetImageSet(createdId)!=null,"Declined deletion removed a set.");
   Delete(true);Require(_repository.GetImageSet(createdId)==null,"Confirmed deletion kept a set.");
   foreach(var image in images)Require(_repository.GetMedia(image.Id)!=null&&File.Exists(image.Path),"Set deletion removed a medium or file.");
   File.WriteAllText(Path.Combine(root,"image-set-crud-result.txt"),"PASS: real create/edit/cancel/add/remove/delete buttons, default No, history/search/keyboard and compact actions; media/files preserved.");
  }
  finally
  {
   Width=oldWidth;if(created is {} id)_repository.DeleteImageSet(id);
   foreach(var image in images)_repository.Delete(image.Id);
   ApplyViewState(before);await _lastRefresh;await _setCoverTask;
  }
 }
}
