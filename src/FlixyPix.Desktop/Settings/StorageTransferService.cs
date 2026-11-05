using System.IO;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

internal sealed record StorageSize(long Libraries,long Shared) { public long Total=>checked(Libraries+Shared); }
internal sealed record StorageTransferRequest(DesktopSettings SourceSettings, string SourceRoot, AppStoragePaths Target,
 bool Copy, bool InitialProfileImport, IReadOnlyList<string>? SelectedLibraries);

/// <summary>Prepares and publishes a storage transfer using the durable journal; UI decisions stay with the caller.</summary>
internal sealed class StorageTransferService(Func<string,CatalogRepository> openCopy, Func<string[],IDisposable> reserve, Action<string>? checkpoint = null)
{

 internal static StorageSize Measure(IEnumerable<string> libraries,string sourceRoot,CancellationToken token)
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
 internal static long? Available(string path)
 {
  try {return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;}
  catch(ArgumentException) {return null;}
  catch(IOException) {return null;}
 }
 internal static void CheckSpace(AppStoragePaths target,StorageSize size)
 {
  var sameVolume=string.Equals(Path.GetPathRoot(target.Root),Path.GetPathRoot(target.ApplicationDirectory),StringComparison.OrdinalIgnoreCase);
  var targetNeeded=checked(size.Libraries*2+size.Shared+64L*1024*1024);
  var appNeeded=checked(size.Shared+4L*1024*1024);
  var required=sameVolume?new[]{(target.Root,checked(targetNeeded+appNeeded))}:new[]{(target.Root,targetNeeded),(target.ApplicationDirectory,appNeeded)};
  foreach(var (root,needed) in required)
   if(Available(root) is long available && available<needed)throw new IOException(T("Not enough free space for the safety copy of the catalog: {0:N0} MB needed, {1:N0} MB free.",needed/1048576,available/1048576));
 }
 internal async Task ApplyAsync(StorageTransferRequest request, CancellationToken token)
 {
  var sourceSettings=request.SourceSettings; var sourceRoot=request.SourceRoot; var target=request.Target;
  var mode=target.Mode; var copy=request.Copy;
  using var reservation = reserve([sourceRoot,target.Root,target.ApplicationDirectory]);
  CheckSpace(target,copy?Measure(request.SelectedLibraries ?? sourceSettings.Libraries.Select(l=>l.Directory).Prepend(sourceSettings.DataDirectory).Distinct(StringComparer.OrdinalIgnoreCase),sourceRoot,token):new StorageSize(0,0));
  var journalPath = Path.Combine(target.ApplicationDirectory,StorageSwitchRecovery.JournalName);
  var journal = StorageJournal.Begin(journalPath,new Dictionary<string,string> { ["app"]=target.ApplicationDirectory,["target"]=target.Root },mode==StorageMode.Portable?"portable":"profile");
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
    if(before==null || request.InitialProfileImport) settings=sourceSettings with {Libraries=settings.Libraries.ToList(),DataDirectory=target.Library};
    var libraries=settings.Libraries.ToList(); string? active=null;
    foreach(var source in request.SelectedLibraries ?? sourceSettings.Libraries.Select(l=>l.Directory).Prepend(sourceSettings.DataDirectory).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
    {
     token.ThrowIfCancellationRequested();
     if(LibraryInstance.IsOpen(source)) throw new IOException(T("Close the other FlixyPix windows first."));
     var relative=Path.Combine("Libraries","import-"+Guid.NewGuid().ToString("N"));
     var destination=Path.Combine(target.Root,relative);
     var stage=journal.AddDirectory("target",relative);
     await LibraryCatalogs.CopyAsync(source,stage,token:token);
     using(var catalog=openCopy(Path.Combine(stage,"catalog.db")))
     {
      catalog.RelocateStoragePaths([new(source,destination),new(sourceRoot,target.Root)]);
      if(!catalog.CheckIntegrity().IsValid) throw new InvalidDataException(T("The copied library failed the integrity check."));
     }
     checkpoint?.Invoke("library-staged");
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
