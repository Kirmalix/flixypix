using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private (StorageMode Mode, bool Copy)? _storageChange;
 private DesktopSettings? _storageSourceSettings;
 private string? _storageSourceRoot;
 private bool _initialProfileImport;
 private Func<string,CatalogRepository>? _storageOpenCopy;
 private Action<string>? _storageCheckpoint;
 private List<string>? _storageSelectedLibraries;
 internal void OfferProfileImport(bool initial = false)
 {
  if (AppStorage.Current.Mode != StorageMode.Portable || _operation != null) return;
  if (initial && Message.Show(this,T("Copy existing profile data into this portable edition? The originals are kept."),T("Data and storage"),MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No) != MessageBoxResult.Yes) return;
  try
  {
   var profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FlixyPix");
   var path=Path.Combine(profile,"appsettings.json");
   if(new FileInfo(path).Length>1024*1024) throw new InvalidDataException(T("The previous settings are unexpectedly large."));
   var document=System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
   using(document)
   {
    var source=System.Text.Json.JsonSerializer.Deserialize<DesktopSettings>(document) ?? throw new InvalidDataException(T("The previous settings are invalid."));
    if(!document.RootElement.TryGetProperty("DataDirectory",out _)) source=source with {DataDirectory=Path.Combine(profile,"Library")};
    if(!Path.IsPathFullyQualified(source.DataDirectory)) throw new InvalidDataException(T("The previous library folder is invalid."));
    ShowStorageChange(source.Validated([]),profile,StorageMode.Portable,initial);
   }
  }
  catch(Exception failure) when(failure is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { Message.Show(this,failure.Message,T("Data and storage")); }
 }
 private void ChangeStorageMode()
 {
  if (!_persistPreferences || _operation != null) return;
  ShowStorageChange(_settings,AppStorage.Current.Root,AppStorage.Current.Mode==StorageMode.Portable?StorageMode.Profile:StorageMode.Portable,false);
 }
 private void ShowStorageChange(DesktopSettings sourceSettings,string sourceRoot,StorageMode mode,bool initialImport)
 {
  var target=AppStoragePaths.Resolve(AppContext.BaseDirectory,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FlixyPix"),mode);
  var dialog=new Window {Owner=this,Title=T("Data and storage"),Width=660,SizeToContent=SizeToContent.Height,MaxHeight=760,WindowStartupLocation=WindowStartupLocation.CenterOwner};
  var panel=new StackPanel {Margin=new Thickness(24)};
  panel.Children.Add(new TextBlock {Text=(mode==StorageMode.Portable?T("Portable"):T("Windows profile"))+"\n"+sourceRoot+" -> "+target.Root,TextWrapping=TextWrapping.Wrap});
  var copy=new RadioButton {Content=T("Copy current libraries; keep the originals"),IsChecked=true,Margin=new Thickness(0,20,0,8)};
  panel.Children.Add(copy);
  var choices=new List<(string Path,CheckBox Box)>();
  foreach(var path in sourceSettings.Libraries.Select(l=>l.Directory).Prepend(sourceSettings.DataDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
  {
   var box=new CheckBox {Content=path,IsChecked=LibraryCatalogs.SameDirectory(path,sourceSettings.DataDirectory)||path.StartsWith(sourceRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),Margin=new Thickness(12,4,0,4)};
   choices.Add((path,box));panel.Children.Add(box);
  }
  var useExisting=new RadioButton {Content=T("Use the existing destination data"),Margin=new Thickness(0,8,0,16)};
  panel.Children.Add(useExisting);
  panel.Children.Add(new TextBlock {Text=T("Existing destination libraries and settings are preserved. Media files are not copied. Other FlixyPix windows must be closed."),TextWrapping=TextWrapping.Wrap});
  panel.Children.Add(new TextBlock {Text=T("Existing translation overrides are kept; screenshot name conflicts receive a new name."),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0)});
  var external=sourceSettings.ExternalPrograms.Select(p=>p.Path).Concat(sourceSettings.QuickAccessFolders).Concat(new[]{sourceSettings.Ffmpeg,sourceSettings.Ffprobe,sourceSettings.Mpv}.OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  if(external.Length>0)panel.Children.Add(new TextBlock {Text=T("External locations remain linked:")+"\n"+string.Join("\n",external),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)});
  var summary=new TextBlock {Margin=new Thickness(0,12,0,0),TextWrapping=TextWrapping.Wrap}; panel.Children.Add(summary);
  var start=new Button {Content=T("Apply and restart"),Margin=new Thickness(0,20,0,0),Padding=new Thickness(12),IsEnabled=false};
  start.Click+=(_,_)=>dialog.DialogResult=true;panel.Children.Add(start);
  dialog.Content=new ScrollViewer {Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
  using var cancellation=new CancellationTokenSource(); int revision=0;
  async void Preview()
  {
   var version=++revision; start.IsEnabled=false;
   var selected=choices.Where(c=>c.Box.IsChecked==true).Select(c=>c.Path).ToArray(); var copying=copy.IsChecked==true;
   try
   {
    var estimate=await Task.Run(()=>copying?MeasureStorage(selected,sourceRoot,cancellation.Token):new StorageSize(0,0),cancellation.Token);
    if(version!=revision||cancellation.IsCancellationRequested) return;
    if(copying&&selected.Length==0) {summary.Text=T("Select at least one library.");return;}
    var free=AvailableStorage(target.Root);
    summary.Text=T("Estimated copy size: {0}",MediaCard.SizeText(estimate.Total))+(free.HasValue?"\n"+T("Free space at destination: {0}",MediaCard.SizeText(free.Value)):"");
    CheckStorageSpace(target,estimate);
    start.IsEnabled=true;
   }
   catch(OperationCanceledException) { }
   catch(Exception failure) when(failure is IOException or UnauthorizedAccessException) { if(version==revision) summary.Text=failure.Message; }
  }
  foreach(var choice in choices) {choice.Box.Checked+=(_,_)=>Preview();choice.Box.Unchecked+=(_,_)=>Preview();}
  copy.Checked+=(_,_)=>Preview(); useExisting.Checked+=(_,_)=>Preview(); dialog.Loaded+=(_,_)=>Preview();
  var accepted=dialog.ShowDialog()==true;cancellation.Cancel();if(!accepted)return;
  try
  {
   using(var check=AppStorage.ReserveTransfer(sourceRoot,target.Root,target.ApplicationDirectory)) { }
   foreach(var library in sourceSettings.Libraries)
    if(!LibraryCatalogs.SameDirectory(library.Directory,_settings.DataDirectory)&&LibraryInstance.IsOpen(library.Directory))throw new IOException(T("Close the other FlixyPix windows first."));
   _storageSelectedLibraries=choices.Where(c=>c.Box.IsChecked==true).Select(c=>c.Path).ToList();
   _storageSourceSettings=sourceSettings;_storageSourceRoot=sourceRoot;_initialProfileImport=initialImport;
   _storageChange=(mode,copy.IsChecked==true);Close();
  }
  catch(Exception failure) when(failure is IOException or UnauthorizedAccessException) {Message.Show(this,failure.Message,T("Data and storage"));}
 }
 private sealed record StorageSize(long Libraries,long Shared) { public long Total=>checked(Libraries+Shared); }
 private static StorageSize MeasureStorage(IEnumerable<string> libraries,string sourceRoot,CancellationToken token)
 {
  long libraryBytes=0,sharedBytes=0;
  foreach(var root in libraries.Concat(new[]{Path.Combine(sourceRoot,"languages"),Path.Combine(sourceRoot,"screenshots")}))
  {
   token.ThrowIfCancellationRequested();
   if(!Directory.Exists(root)) {if(libraries.Contains(root))throw new DirectoryNotFoundException(root);continue;}
   foreach(var file in StorageSourceFiles(root)) {token.ThrowIfCancellationRequested();if(libraries.Contains(root))libraryBytes=checked(libraryBytes+new FileInfo(file).Length);else sharedBytes=checked(sharedBytes+new FileInfo(file).Length);}
  }
  return new(libraryBytes,sharedBytes);
 }
 private static long? AvailableStorage(string path)
 {
  try {return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;}
  catch(ArgumentException) {return null;}
  catch(IOException) {return null;}
 }
 private static void CheckStorageSpace(AppStoragePaths target,StorageSize size)
 {
  var sameVolume=string.Equals(Path.GetPathRoot(target.Root),Path.GetPathRoot(target.ApplicationDirectory),StringComparison.OrdinalIgnoreCase);
  var targetNeeded=checked(size.Libraries*2+size.Shared+64L*1024*1024);
  var appNeeded=checked(size.Shared+4L*1024*1024);
  var required=sameVolume?new[]{(target.Root,checked(targetNeeded+appNeeded))}:new[]{(target.Root,targetNeeded),(target.ApplicationDirectory,appNeeded)};
  foreach(var (root,needed) in required)
   if(AvailableStorage(root) is long available && available<needed)throw new IOException(T("Not enough free space for the safety copy of the catalog: {0:N0} MB needed, {1:N0} MB free.",needed/1048576,available/1048576));
 }
 private async Task ApplyStorageChangeAsync(StorageMode mode, bool copy, AppStoragePaths? destinationOverride = null)
 {
  var sourceSettings = _storageSourceSettings ?? _settings;
  var target = destinationOverride ?? AppStoragePaths.Resolve(AppContext.BaseDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlixyPix"), mode);
  var sourceRoot = _storageSourceRoot ?? AppStorage.Current.Root;
  using var reservation = AppStorage.ReserveTransfer(sourceRoot,target.Root,target.ApplicationDirectory);
  CheckStorageSpace(target,copy?MeasureStorage(_storageSelectedLibraries ?? sourceSettings.Libraries.Select(l=>l.Directory).Prepend(sourceSettings.DataDirectory).Distinct(StringComparer.OrdinalIgnoreCase),sourceRoot,_operation?.Token ?? CancellationToken.None):new StorageSize(0,0));
  var journalPath = Path.Combine(target.ApplicationDirectory,StorageSwitchRecovery.JournalName);
  var journal = StorageJournal.Begin(journalPath,new Dictionary<string,string> { ["app"]=target.ApplicationDirectory,["target"]=target.Root },mode==StorageMode.Portable?"portable":"profile");
  var token=_operation?.Token ?? CancellationToken.None;
  try
  {
   var before=File.Exists(target.Settings)?await File.ReadAllBytesAsync(target.Settings,token):null;
   var settings=before==null ? new DesktopSettings {DataDirectory=target.Library,Privacy=new PrivacySettings {HideRecent=mode==StorageMode.Portable}} :
    System.Text.Json.JsonSerializer.Deserialize<DesktopSettings>(before) ?? throw new InvalidDataException("Invalid destination settings.");
   if(string.IsNullOrWhiteSpace(settings.DataDirectory)||!Path.IsPathFullyQualified(settings.DataDirectory)) settings=settings with {DataDirectory=target.Library};
   settings=settings.Validated([]);
   if(before==null && File.Exists(Path.Combine(target.Library,"catalog.db"))) settings=settings with {Libraries=[new(target.Library)]};
   if(copy)
   {
    if(before==null || _initialProfileImport) settings=sourceSettings with {Libraries=settings.Libraries.ToList(),DataDirectory=target.Library};
    var libraries=settings.Libraries.ToList(); string? active=null;
    foreach(var source in _storageSelectedLibraries ?? sourceSettings.Libraries.Select(l=>l.Directory).Prepend(sourceSettings.DataDirectory).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
    {
     token.ThrowIfCancellationRequested();
     if(LibraryInstance.IsOpen(source)) throw new IOException(T("Close the other FlixyPix windows first."));
     var relative=Path.Combine("Libraries","import-"+Guid.NewGuid().ToString("N"));
     var destination=Path.Combine(target.Root,relative);
     var stage=journal.AddDirectory("target",relative);
     await LibraryCatalogs.CopyAsync(source,stage,token:token);
     using(var catalog=_storageOpenCopy?.Invoke(Path.Combine(stage,"catalog.db")) ?? SecretDialogs.OpenLibrary(null,Path.Combine(stage,"catalog.db")))
     {
      catalog.RelocateStoragePaths([new(source,destination),new(sourceRoot,target.Root)]);
      if(!catalog.CheckIntegrity().IsValid) throw new InvalidDataException(T("The copied library failed the integrity check."));
     }
     _storageCheckpoint?.Invoke("library-staged");
     var known=sourceSettings.Libraries.FirstOrDefault(l=>LibraryCatalogs.SameDirectory(l.Directory,source));
     libraries.Add(new(destination,known?.Name,known?.LastOpened));
     if(LibraryCatalogs.SameDirectory(source,sourceSettings.DataDirectory)) active=destination;
    }
    settings=settings with {DataDirectory=active ?? libraries.Last().Directory,Libraries=libraries};
    StageSharedFiles(journal,sourceRoot,target.Root,token);
   }
   if(LibraryInstance.IsOpen(settings.DataDirectory)) throw new IOException(T("Close the other FlixyPix windows first."));
   if(copy || before==null)
   {
    var data=settings.SerializeFor(target.Root,target.ApplicationDirectory);
    journal.AddFile("target","appsettings.json",data,true);
    journal.AddFile("target","appsettings.last-good.json",data,true);
   }
   // The selector is always last: a new mode cannot point at an unpublished library.
   journal.AddFile("app","storage-mode.json",System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new {mode=mode==StorageMode.Portable?"portable":"profile"}),true);
   journal.MarkDirectoriesReady(); token.ThrowIfCancellationRequested();
   journal.Commit();
  }
  catch
  {
   if(!journal.IsCommitting) journal.Discard();
   // After commit intent leave the durable plan for startup recovery. Never undo
   // a published library or replace a user's intervening edits with an old copy.
   throw;
  }
 }
 private static void StageSharedFiles(StorageJournal journal,string sourceRoot,string targetRoot,CancellationToken token)
 {
  if(LibraryCatalogs.SameDirectory(sourceRoot,targetRoot)) return;
  foreach(var folder in new[]{"languages","screenshots"})
  {
   var source=Path.Combine(sourceRoot,folder);
   if(!Directory.Exists(source)) continue;
   foreach(var file in StorageSourceFiles(source))
   {
    token.ThrowIfCancellationRequested();
    var relative=Path.Combine(folder,Path.GetRelativePath(source,file));
    var target=Path.Combine(targetRoot,relative);
    if(File.Exists(target))
    {
     if(folder=="languages" || File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(file))) continue;
     relative=Path.Combine(Path.GetDirectoryName(relative)!,Path.GetFileNameWithoutExtension(relative)+"-import-"+Guid.NewGuid().ToString("N")+Path.GetExtension(relative));
    }
    journal.AddFile("target",relative,File.ReadAllBytes(file),false);
   }
  }
 }
 private static IEnumerable<string> StorageSourceFiles(string directory)
 {
  if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0) throw new IOException("Linked storage folders cannot be copied.");
  foreach(var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
  {
   if((entry.Attributes&FileAttributes.ReparsePoint)!=0) throw new IOException("Linked storage files cannot be copied.");
   if(entry is DirectoryInfo) { foreach(var file in StorageSourceFiles(entry.FullName)) yield return file; }
   else yield return entry.FullName;
  }
 }
}
