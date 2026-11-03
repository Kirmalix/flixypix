using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlixyPix.Core;
using ImageMagick;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private async Task RunDuplicateSearchSmokeAsync(string directory)
 {
  static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
  static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  foreach(var encrypted in new[]{false,true})
  {
   var root=Path.Combine(directory,encrypted?"duplicate-hub-encrypted":"duplicate-hub-plain");Directory.CreateDirectory(root);var path=Path.Combine(root,"catalog.db");using(var empty=new CatalogRepository(path)){}
   if(encrypted)CatalogEncryption.EnableEncryption(path,"synthetic-duplicate-hub-password");using var key=encrypted?CatalogEncryption.Unlock(path,"synthetic-duplicate-hub-password"):null;using var db=new CatalogRepository(path,key);
   var original=Path.Combine(root,"original.png");var exact=Path.Combine(root,"exact.png");var similar=Path.Combine(root,"similar.jpg");
   using(var image=new MagickImage("plasma:fractal",640,480))image.Write(original);File.Copy(original,exact);
   using(var image=new MagickImage(original)){image.Resize(320,240);image.Quality=60;image.Write(similar,MagickFormat.Jpeg);}
   MediaItem Add(string file)=>db.Upsert(new(){Path=file,Kind=MediaKind.Image,Title=Path.GetFileName(file)});
   var a=Add(original);var b=Add(exact);var c=Add(similar);var missing=Add(Path.Combine(root,"missing.png"));
   var before=new[]{original,exact,similar}.ToDictionary(p=>p,p=>SHA256.HashData(File.ReadAllBytes(p)));
   var window=new DuplicateSearchWindow(this,db,[a,c],selectionScope:true);window.Show();
   try
   {
    Require(window.Result==null,"Opening duplicate hub started a scan.");Click(window.ExactButton);await window.Searching;Require(window.Result is {Groups.Count:0,Errors.Count:0},"Selection SHA scan included outside exact copy or visually similar JPEG.");
    window.SelectionScope=false;Click(window.ExactButton);await window.Searching;Require(window.Result is {Groups.Count:1,Errors.Count:1}&&window.Result.Groups[0].Items.Select(i=>i.Id).ToHashSet().SetEquals([a.Id,b.Id]),"Whole-library SHA scan lost exact group or missing-file report.");
    await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Capture((FrameworkElement)window.Content,Path.Combine(root,"exact-results.png"));
    Click(window.ExactButton);await window.Searching;Require(window.Result?.Groups.Count==1,"Repeated SHA scan changed the result.");
    Click(window.ExactButton);Click(window.CancelButton);await window.Searching;Require(window.Result==null&&window.StatusText==T("Cancelled")&&window.ExactButton.IsEnabled,"Actual scan cancellation retained stale result or blocked restart.");
    window.SelectionScope=true;var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    window.OpenSimilar=async selected=>
    {
     SimilarMediaWindow? visual=null;
     try
     {
      Require(selected!=null&&selected.Select(i=>i.Id).ToHashSet().SetEquals([a.Id,c.Id]),"Hub forwarded wrong visual scope.");
      visual=new SimilarMediaWindow(window,db,null,(_,_)=>throw new InvalidOperationException("Scan tried to delete."),scope:selected);visual.Show();visual.Choose(MediaKind.Image,SimilarityLevel.Similar);await visual.SearchAsync();
      var group=visual.Groups.Single();Require(group.Items.Select(i=>i.Id).ToHashSet().SetEquals([a.Id,c.Id]),"Scoped visual scan included outside exact copy or omitted recompressed JPEG.");
      await visual.PicturesReady;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Capture((FrameworkElement)visual.Content,Path.Combine(root,"visual-selection.png"));visual.Close();
      db.SaveFingerprint(new(missing.Id,"stale-missing-stamp",[FlixyPix.Imaging.ImageFingerprints.Hash(original)]));
      visual=new SimilarMediaWindow(window,db,null,(_,_)=>throw new InvalidOperationException("Scan tried to delete."));visual.Show();visual.Choose(MediaKind.Image,SimilarityLevel.Similar);await visual.SearchAsync();
      Require(visual.Groups.All(g=>g.Items.All(i=>i.Id!=missing.Id))&&Descendants<Expander>((FrameworkElement)visual.Content).Single(expander=>Equals(expander.Header,T("Not checked:"))).Visibility==Visibility.Visible,"Missing file with stale matching fingerprint was included or not reported.");completed.TrySetResult();
     }
     catch(Exception error){completed.TrySetException(error);}
     finally{visual?.Close();}
    };
    Click(window.SimilarButton);await completed.Task;
    Require(db.Search().Count==4,"Scanning changed catalog membership.");foreach(var(file,hash)in before)Require(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(file))),"Scan changed source bytes.");
    window.SelectionScope=false;Click(window.ExactButton);await window.Searching;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
    var navigate=Descendants<Button>((FrameworkElement)window.Content).First(button=>Equals(button.Content,T("Show in library")));Click(navigate);Require(window.NavigationTarget?.Id is {} id&&(id==a.Id||id==b.Id),"Comparison navigation lost selected media.");
    File.WriteAllText(Path.Combine(root,"result.txt"),"PASS: explicit hub entry, library/selection SHA equality vs recompressed visual similarity, missing files, repeat, actual cancel/restart, scoped visual bridge, comparison/navigation and unchanged sources/catalog membership.");
   }
   finally{window.Close();}
  }
 }
}
