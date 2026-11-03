using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private async Task RunPublicationPickerSmokeAsync(string directory)
 {
  static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
  static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  foreach(var encrypted in new[]{false,true})
  {
   var root=Path.Combine(directory,encrypted?"group-picker-encrypted":"group-picker-plain");Directory.CreateDirectory(root);var path=Path.Combine(root,"catalog.db");
   using(var empty=new CatalogRepository(path)){}
   if(encrypted)CatalogEncryption.EnableEncryption(path,"synthetic-group-picker-password");
   using var key=encrypted?CatalogEncryption.Unlock(path,"synthetic-group-picker-password"):null;using var db=new CatalogRepository(path,key);
   var videos=Enumerable.Range(0,131).Select(i=>db.Upsert(new(){Path=Path.Combine(root,i+".mp4"),Title="Synthetic "+i.ToString("D3"),Kind=MediaKind.Video})).ToArray();
   var image=db.Upsert(new(){Path=Path.Combine(root,"image.jpg"),Kind=MediaKind.Image});
   var set=db.UpsertImageSet(new(){Id=videos[0].Id,Name="Ordered images",MediaIds=[image.Id]});
   var group=db.UpsertPublicationGroup(new(){Name="Picker group",Members=[new(PublicationGroupMemberKind.Video,videos[0].Id)]});
   var expected=new[]{new PublicationGroupMember(PublicationGroupMemberKind.Video,videos[1].Id),new(PublicationGroupMemberKind.Video,videos[119].Id),new(PublicationGroupMemberKind.Video,videos[122].Id),new(PublicationGroupMemberKind.ImageSet,set.Id),new(PublicationGroupMemberKind.Video,videos[130].Id)};
   var window=new PublicationGroupMembersWindow(this,db,group.Members,members=>Task.Run(()=>db.AddPublicationGroupMembers(group.Id,members)));Exception? failure=null;
   window.ContentRendered+=async(_,_)=>
   {
    try
    {
     await window.LoadTask;Require(window.Rows.Items.Count==120&&window.Next.IsEnabled,"Picker did not limit initial video page to120.");
     Require(window.Rows.Items[0] is CheckBox {IsEnabled:false,IsChecked:true},"Existing member was not checked/disabled.");
     ((CheckBox)window.Rows.Items[1]).IsChecked=true;((CheckBox)window.Rows.Items[119]).IsChecked=true;
     Click(window.Next);await window.LoadTask;Require(window.Rows.Items.Count==11&&!window.Next.IsEnabled,"Picker second page is incorrect.");
     ((CheckBox)window.Rows.Items[2]).IsChecked=true;
     window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window)!,0,Key.PageUp){RoutedEvent=Keyboard.PreviewKeyDownEvent});await window.LoadTask;
     Require(window.Rows.Items.Count==120&&window.Rows.Items[1] is CheckBox{IsChecked:true},"PageUp lost page/selection.");
     window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window)!,0,Key.PageDown){RoutedEvent=Keyboard.PreviewKeyDownEvent});await window.LoadTask;
     Require(window.Rows.Items.Count==11&&window.Rows.Items[2] is CheckBox{IsChecked:true},"PageDown lost page/selection.");
     window.Kind.SelectedIndex=1;await window.LoadTask;Require(window.Rows.Items.Count==1&&window.Rows.Items[0] is CheckBox {IsEnabled:true,IsChecked:false},"Same GUID image set collided with existing video.");
     ((CheckBox)window.Rows.Items[0]).IsChecked=true;
     window.Kind.SelectedIndex=0;await window.LoadTask;Require(window.Rows.Items[1] is CheckBox {IsChecked:true},"Selection lost after changing kind/page.");
     window.Search.Text="Synthetic 130";
     window.Search.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window.Search)!,0,Key.Enter){RoutedEvent=Keyboard.KeyDownEvent});await window.LoadTask;
     Require(window.Rows.Items.Count==1,"Picker text search was not applied.");((CheckBox)window.Rows.Items[0]).IsChecked=true;
     Require(window.Selected.SequenceEqual(expected),"Selection order across pages/kinds/search changed.");
     Capture((FrameworkElement)window.Content,Path.Combine(root,"selected-members.png"));Click(window.ApplyButton);await window.ApplyTask;
     if(window.IsVisible)throw new InvalidOperationException(window.FailureText);
    }
    catch(Exception error){failure=error;window.Close();}
   };
   if(window.ShowDialog()!=true||failure!=null)throw new InvalidOperationException("Publication picker apply failed.",failure);
   Require(db.GetPublicationGroup(group.Id)!.Members.SequenceEqual(group.Members.Concat(expected)),"Picker did not save only selected members in order.");
   var cancelled=new PublicationGroupMembersWindow(this,db,[],_=>throw new InvalidOperationException("Cancel must not write"));
   cancelled.ContentRendered+=(_,_)=>cancelled.Close();cancelled.ShowDialog();await cancelled.LoadTask;
   Require(db.GetPublicationGroup(group.Id)!.Members.Count==6,"Cancelled picker changed membership.");
   var rejected=new PublicationGroupMembersWindow(this,db,[],_=>throw new InvalidOperationException("Synthetic stale group"));rejected.Show();await rejected.LoadTask;
   try
   {
    ((CheckBox)rejected.Rows.Items[0]).IsChecked=true;Click(rejected.ApplyButton);await rejected.ApplyTask;
    Require(rejected.IsVisible&&rejected.Selected.Count==1&&rejected.FailureText.Contains("Synthetic stale"),"Failed apply discarded picker selection.");
   }
   finally{rejected.Close();}
   File.WriteAllText(Path.Combine(root,"result.txt"),"PASS:120/11 SQL video paging, search, typed ID collision, ordered selection across pages/kinds, actual apply, cancel and failure retention.");
  }
 }
}
