using System.Text.Json;

namespace FlixyPix.Core;

public enum StorageMode { Portable, Profile }

/// <summary>Resolved once before any desktop preferences or native components are loaded.</summary>
public sealed record AppStoragePaths(StorageMode Mode, string ApplicationDirectory, string Root)
{
 public string Settings => Path.Combine(Root, "appsettings.json");
 public string Library => Path.Combine(Root, "Library");
 public string Libraries => Path.Combine(Root, "Libraries");
 public string Logs => Path.Combine(Root, "logs");
 public string Languages => Path.Combine(Root, "languages");
 public string Screenshots => Path.Combine(Root, "screenshots");
 public string Temp => Path.Combine(Root, "temp");
 public static AppStoragePaths Resolve(string applicationDirectory, string profileDirectory, StorageMode? forced = null)
 {
  var app = Path.GetFullPath(applicationDirectory);
  var selector = Path.Combine(app, "storage-mode.json");
  var mode = forced;
  if (mode is null && File.Exists(selector))
  {
   using var json = JsonDocument.Parse(File.ReadAllText(selector));
   var value = json.RootElement.GetProperty("mode").GetString();
   mode = value switch { "portable" => StorageMode.Portable, "profile" => StorageMode.Profile,
    _ => throw new InvalidDataException("Invalid storage mode. Start with --portable or --profile to recover.") };
  }
  mode ??= File.Exists(Path.Combine(app, "portable.flag")) ? StorageMode.Portable : StorageMode.Profile;
  return new(mode.Value, app, mode == StorageMode.Portable ? Path.Combine(app, "Data") : Path.GetFullPath(profileDirectory));
 }
 public void RememberMode(StorageMode mode)
 {
  var path = Path.Combine(ApplicationDirectory, "storage-mode.json");
  FileAccessPolicy.Current.EnsureWritable(path);
  var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
  try { File.WriteAllText(temporary, JsonSerializer.Serialize(new { mode = mode == StorageMode.Portable ? "portable" : "profile" })); File.Move(temporary, path, true); }
  finally { if (File.Exists(temporary)) File.Delete(temporary); }
 }
}

public static class AppStorage
{
 public static AppStoragePaths Current { get; private set; } = AppStoragePaths.Resolve(AppContext.BaseDirectory,
  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlixyPix"), StorageMode.Profile);
 private static StorageRootSession? rootSession;
 private static FileStream? workspaceLease;
 private static string? workspace;

 public static IDisposable ReserveTransfer(params string[] roots)
 {
  if(rootSession == null) throw new InvalidOperationException("Storage has not been initialized.");
  return rootSession.Reserve(roots);
 }
 public static void EnsureNoOtherInstances(string root) { using var reservation = ReserveTransfer(root); }
 public static void Release()
 {
  workspaceLease?.Dispose(); workspaceLease = null;
  if(workspace != null) { CleanupWorkspace(workspace,Current.Temp); workspace = null; }
  rootSession?.Dispose(); rootSession = null;

 }
 public static string WorkDirectory { get { var path = workspace ?? Path.Combine(Path.GetTempPath(), "FlixyPix", "work"); Directory.CreateDirectory(path); return path; } }
 public static void Initialize(AppStoragePaths paths)
 {
  FileAccessPolicy.Current.EnsureWritable(paths.Root);
  rootSession = new StorageRootSession(paths.Root);
  Directory.CreateDirectory(paths.Temp);
  // Probe the actual destination; never fall back to the host profile.
  var probe = Path.Combine(paths.Temp, Guid.NewGuid().ToString("N") + ".tmp");
  using (File.Create(probe)) { } File.Delete(probe);
  Current = paths;
  foreach(var stale in Directory.EnumerateDirectories(paths.Temp,"session-*"))
  {
   try { using (new FileStream(Path.Combine(stale,"owner.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)) { } CleanupWorkspace(stale,paths.Temp); }
   catch(IOException) { }
   catch(UnauthorizedAccessException) { }
  }
  workspace = Path.Combine(paths.Temp,"session-" + Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(workspace);
  workspaceLease = new FileStream(Path.Combine(workspace,"owner.lock"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
  foreach (var name in new[] { "TEMP", "TMP", "SQLITE_TMPDIR", "MAGICK_TEMPORARY_PATH" })
   Environment.SetEnvironmentVariable(name, workspace, EnvironmentVariableTarget.Process);
 }

 private static void CleanupWorkspace(string directory,string parent)
 {
  if(!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(directory).StartsWith("session-",StringComparison.Ordinal)) return;
  try { Check(directory); Directory.Delete(directory,true); }
  catch(IOException) { }
  catch(UnauthorizedAccessException) { }
  static void Check(string path)
  {
   if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new IOException("Linked workspace.");
   foreach(var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
   {
    if((entry.Attributes&FileAttributes.ReparsePoint)!=0) throw new IOException("Linked workspace entry.");
    if(entry is DirectoryInfo) Check(entry.FullName);
   }
  }
 }
}
