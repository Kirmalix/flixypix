using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FlixyPix.Core;
namespace FlixyPix.Desktop;
public partial class MainWindow
{
 internal async Task RunStorageTransitionSmokeAsync(string directory)
 {
  static void Require(bool value,string text) { if(!value) throw new InvalidOperationException(text); }
  var root=Path.Combine(directory,"storage-transition"); Directory.CreateDirectory(root);
  var settingsWindow=new SettingsWindow(this,_repository,_settings,_settings.DataDirectory,()=>{},()=>{},storage:()=>{},importProfile:()=>{});
  try
  {
   settingsWindow.Show();settingsWindow.SelectPage("Storage");
   await Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
   Capture((System.Windows.FrameworkElement)settingsWindow.Content,Path.Combine(root,"storage-settings.png"));
   Require(settingsWindow.Search(T("Copy profile data"))>=1,"Storage import was not discoverable in settings search.");
  }
  finally {settingsWindow.Close();}
  var shared=Path.Combine(root,"source");
  var source=Path.Combine(shared,"Library"); Guid id;
  using(var catalog=new CatalogRepository(Path.Combine(source,"catalog.db")))
  {
   var item=new MediaItem {Path=Path.Combine(source,"original.mp4")}; id=item.Id; catalog.Upsert(item);
  }
  var sourceHash=SHA256.HashData(File.ReadAllBytes(Path.Combine(source,"catalog.db")));
  var app=Path.Combine(root,"application"); Directory.CreateDirectory(app);
  var previousSource=_storageSourceSettings; var previousSelection=_storageSelectedLibraries;var previousRoot=_storageSourceRoot;
  var secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
  Directory.CreateDirectory(Path.Combine(shared,"languages"));Directory.CreateDirectory(Path.Combine(shared,"screenshots"));
  File.WriteAllText(Path.Combine(shared,"languages","de.json"),"source override");File.WriteAllText(Path.Combine(shared,"screenshots","shot.png"),"source screenshot");
  _storageOpenCopy=path=> {if(!CatalogEncryption.IsEncrypted(path))return new CatalogRepository(path);using var key=CatalogEncryption.Unlock(path,secret);return new CatalogRepository(path,key);};
  try
  {
   _storageSourceSettings=new DesktopSettings {DataDirectory=source,Libraries=[new(source)]};
   _storageSelectedLibraries=[source];_storageSourceRoot=shared;
   foreach(var mode in new[]{StorageMode.Profile,StorageMode.Portable})
   {
    var destination=new AppStoragePaths(mode,app,mode==StorageMode.Portable?Path.Combine(app,"Data"):Path.Combine(root,"Profile"));
    if(mode==StorageMode.Portable) {CatalogEncryption.EnableEncryption(Path.Combine(source,"catalog.db"),secret);sourceHash=SHA256.HashData(File.ReadAllBytes(Path.Combine(source,"catalog.db")));}
    Directory.CreateDirectory(destination.Root);
    Directory.CreateDirectory(destination.Languages);Directory.CreateDirectory(destination.Screenshots);
    File.WriteAllText(Path.Combine(destination.Languages,"de.json"),"destination override");File.WriteAllText(Path.Combine(destination.Screenshots,"shot.png"),"destination screenshot");
    var existing=new DesktopSettings {DataDirectory=Path.Combine(destination.Root,"existing"),Language="fr",Libraries=[new(Path.Combine(destination.Root,"existing"))]}; existing.SaveTo(destination.Settings);
    await ApplyStorageChangeAsync(mode,true,destination);
    var settings=JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(destination.Settings))!;
    Require(settings.Language=="fr","Storage copy replaced destination preferences.");
    Require(settings.DataDirectory!=source && settings.Libraries.Count==2,"Storage copy did not register the copied library.");
    using(var copied=_storageOpenCopy(Path.Combine(settings.DataDirectory,"catalog.db")))
     Require(copied.GetMedia(id)!.Path.StartsWith(settings.DataDirectory,StringComparison.OrdinalIgnoreCase),"Storage copy retained internal source paths.");
    Require(SHA256.HashData(File.ReadAllBytes(Path.Combine(source,"catalog.db"))).SequenceEqual(sourceHash),"Storage copy changed the original catalog.");
    Require(AppStoragePaths.Resolve(app,destination.Root).Mode==mode,"Storage mode was not remembered.");
    Require(File.ReadAllText(Path.Combine(destination.Languages,"de.json"))=="destination override","A target translation was overwritten.");
    Require(Directory.GetFiles(destination.Screenshots).Length==2,"A conflicting screenshot was lost.");
    var before=File.ReadAllBytes(destination.Settings);
    await ApplyStorageChangeAsync(mode,false,destination);
    Require(before.SequenceEqual(File.ReadAllBytes(destination.Settings)),"Using existing data rewrote destination preferences.");
    _operation=new CancellationTokenSource();_storageCheckpoint=_=>_operation.Cancel();
    try {await ApplyStorageChangeAsync(mode,true,destination);throw new InvalidOperationException("Cancelled transfer completed.");}
    catch(OperationCanceledException) { }
    finally {_storageCheckpoint=null;_operation.Dispose();_operation=null;}
    Require(before.SequenceEqual(File.ReadAllBytes(destination.Settings)),"Cancellation changed destination preferences.");
    Require(!Directory.Exists(Path.Combine(app,StorageSwitchRecovery.JournalName)),"Cancelled preparation left a pending journal.");
   }
  }
  finally { _storageSourceSettings=previousSource; _storageSelectedLibraries=previousSelection;_storageSourceRoot=previousRoot;_storageOpenCopy=null;_storageCheckpoint=null; }
  foreach(var phase in new[]{"prepared","database-committed","file-published"})
  {
   var fixture=Path.Combine(root,"crash-"+phase);Directory.CreateDirectory(fixture);
   File.WriteAllText(Path.Combine(fixture,"test-owner.txt"),"flixypix-storage-crash-fixture");
   File.WriteAllText(Path.Combine(fixture,"phase.txt"),phase);
   var start=new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=AppContext.BaseDirectory};
   start.ArgumentList.Add("--storage-crash-test");start.ArgumentList.Add(fixture);
   using var child=System.Diagnostics.Process.Start(start) ?? throw new IOException("The crash-test child did not start.");
   using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
   try {await child.WaitForExitAsync(timeout.Token);}
   finally {if(!child.HasExited)child.Kill(entireProcessTree:true);}
   Require(child.ExitCode!=0 && File.ReadAllText(Path.Combine(fixture,"kill-marker.txt"))==phase,"The child did not terminate at its crash checkpoint.");
   using var recovered=new CatalogRepository(Path.Combine(fixture,"catalog","catalog.db"));
   var moved=Path.Combine(fixture,"old","child");recovered.PreparePortableLocation(moved,true);
   Require(recovered.GetMedia(StorageCrashProbe.MediaId)!.Path==Path.Combine(moved,"media.mp4"),"A killed process left an incorrect media path.");
   using var queue=JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture,"catalog","queue.json")));
   Require(queue.RootElement.GetProperty("input").GetString()==Path.Combine(moved,"media.mp4"),"A killed process left an incorrect queue path.");
  }
 }
}
