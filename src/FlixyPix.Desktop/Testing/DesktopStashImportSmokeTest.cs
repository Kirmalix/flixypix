using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Threading;
using FlixyPix.Core;
using ImageMagick;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Drives the Stash wizard through all steps against its own catalog: first import with an archive image, an unchanged
 /// repeat that proposes nothing, and a changed title that is only taken once its field is chosen.</summary>
 private async Task RunStashImportSmokeAsync(string directory)
 {
  static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
  var root=Path.Combine(directory,"stash-import");var export=Path.Combine(root,"export");var media=Path.Combine(root,"media");
  Directory.CreateDirectory(media);
  void Add(string relative,string json){var path=Path.Combine(export,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,json);}
  File.WriteAllText(Path.Combine(media,"a.mp4"),"synthetic video");
  var picture=Path.Combine(root,"a.png");using(var image=new MagickImage("plasma:fractal",64,48))image.Write(picture);
  using(var zip=ZipFile.Open(Path.Combine(media,"photos.zip"),ZipArchiveMode.Create))zip.CreateEntryFromFile(picture,"inner/a.png");
  Add("performers/jane.json","""{"name":"Jane","aliases":"J. Doe"}""");Add("tags/outdoor.json","""{"name":"Outdoor"}""");
  Add("scenes/a.json","""{"title":"Stash scene","files":["/library/a.mp4"],"tags":["Outdoor"],"performers":["Jane"],"markers":[{"title":"Mark","seconds":"1.5"}]}""");
  Add("galleries/photos.json","""{"title":"Photos","zip_files":["/library/photos.zip"]}""");
  Add("images/a.json","""{"title":"Zipped","files":["/library/photos.zip/inner/a.png"],"galleries":[{"zip_files":["/library/photos.zip"]}]}""");
  Add("files/a.json",$$"""{"type":"image","path":"/library/photos.zip/inner/a.png","size":{{new FileInfo(picture).Length}},"zip_file":"/library/photos.zip"}""");
  var path=Path.Combine(root,"catalog.db");using var db=new CatalogRepository(path);
  async Task<StashImportWindow> Prepare()
  {
   var window=OpenStashImport(db);window.Show();
   await window.ReadAsync(export);window.SetMappings(("/library",media));await window.PreviewAsync();return window;
  }
  var first=await Prepare();
  try
  {
   Require(first.Rows.Count(r=>r.Include)==5&&first.Rows.All(r=>r.Include||r.Preview.Draft.Kind==StashEntityKind.SavedFilter),"The first Stash preview did not propose every new record.");
   await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);Capture((FrameworkElement)first.Content,Path.Combine(root,"stash-selection.png"));
   await first.ApplyAsync();
   Require(first.Report.StartsWith(T("New: {0:N0} · Updated: {1:N0} · Unchanged: {2:N0}",5,0,0),StringComparison.Ordinal),"Stash import report: "+first.Report+" · "+first.StatusText);
   var video=db.Search().Single(m=>m.Kind==MediaKind.Video);var zipped=db.Search().Single(m=>m.Kind==MediaKind.Image);
   Require(video.TagCategories.Values.Any(names=>names.Contains("Jane"))&&video.Tags.Contains("Outdoor")&&db.GetBookmarks(video.Id).Count==1,"Stash scene lost performer, tag or marker.");
   Require(zipped.Extra.GetValueOrDefault("entryName")=="inner/a.png"&&File.Exists(zipped.Path)&&Assert(db.GetImageSets())==zipped.Id,"Stash archive image or gallery was not imported.");
   Capture((FrameworkElement)first.Content,Path.Combine(root,"stash-report.png"));
  }
  finally{first.Close();}
  var repeat=await Prepare();
  try{Require(repeat.Rows.All(r=>!r.Include)&&repeat.Rows.Where(r=>r.Selectable).All(r=>r.Status==T("Unchanged")),"An unchanged Stash repeat proposed records.");}
  finally{repeat.Close();}
  Add("scenes/a.json","""{"title":"Renamed in Stash","files":["/library/a.mp4"],"tags":["Outdoor"],"performers":["Jane"],"markers":[{"title":"Mark","seconds":"1.5"}]}""");
  var changed=await Prepare();
  try
  {
   var scene=changed.Rows.Single(r=>r.Preview.Draft.Kind==StashEntityKind.Scene);Require(scene.Status==T("Changed")&&!scene.Include,"A changed Stash scene was not shown as changed or was preselected.");
   scene.Include=true;await changed.ApplyAsync();
   Require(db.Search().Single(m=>m.Kind==MediaKind.Video).Title=="Stash scene","A Stash field was taken without being chosen.");
  }
  finally{changed.Close();}
  var chosen=await Prepare();
  try
  {
   var scene=chosen.Rows.Single(r=>r.Preview.Draft.Kind==StashEntityKind.Scene);
   Require(scene.Preview.Fields.Single(f=>f.Field=="title").Status==StashFieldStatus.PreviouslyKept,"The kept Stash title was not remembered as decided.");
   scene.UseSource.Add("title");scene.Include=true;await chosen.ApplyAsync();
   Require(db.Search().Single(m=>m.Kind==MediaKind.Video).Title=="Renamed in Stash","The chosen Stash title was not taken.");
  }
  finally{chosen.Close();}
  static Guid Assert(IReadOnlyList<ImageSet> sets)=>sets is [{MediaIds:[var only]}]?only:Guid.Empty;
 }
}
