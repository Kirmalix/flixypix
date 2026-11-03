using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private async Task RunImageSetProposalsSmokeAsync(string directory)
 {
  foreach(var encrypted in new[]{false,true})
  {
   var root=Path.Combine(directory,encrypted?"set-proposals-encrypted":"set-proposals-plain");Directory.CreateDirectory(root);
   var path=Path.Combine(root,"catalog.db");using(var empty=new CatalogRepository(path)){}
   if(encrypted)CatalogEncryption.EnableEncryption(path,"synthetic-set-modal-secret");
   using var key=encrypted?CatalogEncryption.Unlock(path,"synthetic-set-modal-secret"):null;
   using var db=new CatalogRepository(path,key);
   var first=db.Upsert(new(){Path=Path.Combine(root,"folder","one.jpg"),Title="Folder image",Kind=MediaKind.Image});
   var second=db.Upsert(new(){Path=Path.Combine(root,"cache","two.jpg"),Title="Archive image",Kind=MediaKind.Image,Extra=new(){["archivePath"]=Path.Combine(root,"source.zip"),["entryName"]="page.jpg"}});
   var preview=db.GetImageSetProposals();var window=new ImageSetProposalsWindow(this,preview,db.GetMedia,selected=>Task.Run(()=>db.ApplyImageSetProposals(selected)),db.GetImageSet);
   Exception? failure=null;
   window.ContentRendered+=async(_,_)=>
   {
    try
    {
     window.Choices.Single(c=>c.Proposal.IsArchive).Include.IsChecked=false;
     window.Choices.Single(c=>!c.Proposal.IsArchive).Name.Text="Confirmed folder";
     Capture((FrameworkElement)window.Content,Path.Combine(root,"selected-preview.png"));
     window.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await window.ApplyTask;
     if(window.IsVisible)throw new InvalidOperationException(window.FailureText);
    }
    catch(Exception error){failure=error;window.Close();}
   };
   if(window.ShowDialog()!=true||failure!=null)throw new InvalidOperationException("Image-set proposal modal apply failed.",failure);
   var created=db.GetImageSets().Single();
   if(created.Name!="Confirmed folder"||!created.MediaIds.SequenceEqual([first.Id]))throw new InvalidOperationException("Unchecked image-set proposal was created or selected proposal changed.");
   db.UpsertImageSet(new(){Name="Shared reference",MediaIds=[first.Id,second.Id]});
   var cancel=new ImageSetProposalsWindow(this,db.GetImageSetProposals(),db.GetMedia,_=>throw new InvalidOperationException("Cancel must not apply"),db.GetImageSet);
   cancel.ContentRendered+=(_,_)=>
   {
    try
    {
     if(cancel.Choices.Single(c=>!c.Proposal.IsArchive).Include.IsChecked!=false||cancel.Choices.Single(c=>c.Proposal.IsArchive).Include.IsChecked!=true)throw new InvalidOperationException("Duplicate and partial-overlap defaults are wrong.");
     var labels=Descendants<TextBlock>((FrameworkElement)cancel.Content).Select(t=>t.Text).ToArray();
     if(!labels.Any(t=>t.Contains("Confirmed folder"))||!labels.Any(t=>t.Contains("Shared reference")))throw new InvalidOperationException("Existing/overlapping set names are missing.");
     Descendants<Expander>((FrameworkElement)cancel.Content).First().IsExpanded=true;
     Capture((FrameworkElement)cancel.Content,Path.Combine(root,"existing-overlap-preview.png"));
    }
    catch(Exception error){failure=error;}
    cancel.Close();
   };
   cancel.ShowDialog();if(failure!=null)throw failure;
   if(db.GetImageSets().Count!=2||db.GetMedia(second.Id)==null)throw new InvalidOperationException("Cancel changed sets or media.");
   var rejected=new ImageSetProposalsWindow(this,db.GetImageSetProposals(),db.GetMedia,_=>throw new InvalidOperationException("Synthetic stale proposals"),db.GetImageSet);
   rejected.Show();
   try
   {
    var archiveChoice=rejected.Choices.Single(c=>c.Proposal.IsArchive);archiveChoice.Name.Text="Retained choice";
    rejected.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await rejected.ApplyTask;
    if(!rejected.IsVisible||!rejected.FailureText.Contains("Synthetic stale")||archiveChoice.Name.Text!="Retained choice"||archiveChoice.Include.IsChecked!=true)
     throw new InvalidOperationException("Rejected proposal discarded the dialog selection.");
   }
   finally{rejected.Close();}
   File.WriteAllText(Path.Combine(root,"result.txt"),"PASS: confirmed selection, edited name, unchecked archive, duplicate default and cancellation.");
  }
  await Task.CompletedTask;
 }
}
