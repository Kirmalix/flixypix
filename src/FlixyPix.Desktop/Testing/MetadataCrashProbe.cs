using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Owned synthetic fixture only: kill after a UI checkpoint, then reopen and use the actual recovery dialog.</summary>
internal static class MetadataCrashProbe
{
 internal const string Owner = "flixypix-metadata-crash-fixture";
 internal const string Password = "synthetic-metadata-fixture-password";
 internal const string DraftText = "Synthetic UI draft survives forced process termination";
 internal static readonly Guid MediaId = new("65764cca-10df-4881-acb4-ab1d9ee71977");

 internal static async Task RunAsync(string root)
 {
  if (File.ReadAllText(Path.Combine(root, "test-owner.txt")) != Owner) throw new InvalidDataException("Not a metadata fixture.");
  var phase = File.ReadAllText(Path.Combine(root, "phase.txt"));
  if (phase is not ("write" or "recover" or "role" or "role-light")) throw new InvalidDataException("Invalid metadata fixture phase.");
  var library = Path.Combine(root, "catalog"); var database = Path.Combine(library, "catalog.db");
  using var key = CatalogEncryption.IsEncrypted(database) ? CatalogEncryption.Unlock(database, Password) : null;
  if (phase is "role" or "role-light") { await MainWindow.RunRoleRestoreProbeAsync(root, key, phase == "role-light" ? ColorScheme.Light : ColorScheme.Dark); return; }
  var window = new MainWindow(new DesktopSettings { DataDirectory = library, AutoGeneratePreviews = false }, persistPreferences: false, unlockedKey: key);
  var recovered = false;
  window.RecoveryOpened = recovery =>
  {
   if (phase != "recover") throw new InvalidOperationException("Unexpected recovery dialog.");
   MainWindow.Capture((FrameworkElement)recovery.Content, Path.Combine(root, "recovery.png"));
   recovered = true; recovery.RestoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  };
  var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  window.Loaded += async (_, _) =>
  {
   try
   {
    await window.RunMetadataCrashPhaseAsync(phase, root, () => recovered);
    completed.TrySetResult();
   }
   catch (Exception error) { completed.TrySetException(error); }
  };
  window.Show(); await completed.Task;
  window.Close();
 }
}

public partial class MainWindow
{
 internal async Task RunMetadataCrashPhaseAsync(string phase, string root, Func<bool> recovered)
 {
  await Startup;
  await RefreshAsync(MetadataCrashProbe.MediaId);
  if (phase == "write")
  {
   EditDescription.Text = MetadataCrashProbe.DraftText;
   CheckpointDetailsDrafts();
   File.WriteAllText(Path.Combine(root, "kill-marker.txt"), "checkpointed");
   Process.GetCurrentProcess().Kill();
   throw new InvalidOperationException("Synthetic child did not terminate.");
  }
  for (var count = 0; !recovered() && count < 100; count++) await Task.Delay(50);
  await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  await RefreshAsync(MetadataCrashProbe.MediaId);
  if (!recovered() || EditDescription.Text != MetadataCrashProbe.DraftText || _repository.GetMedia(MetadataCrashProbe.MediaId)!.Description != null)
   throw new InvalidOperationException("UI crash recovery did not restore an unsaved draft separately from saved media.");
  SaveDetails(this, new RoutedEventArgs()); await _lastRefresh;
  if (_repository.GetMedia(MetadataCrashProbe.MediaId)!.Description != MetadataCrashProbe.DraftText || _repository.GetMetadataDrafts().Count != 0)
   throw new InvalidOperationException("Recovered UI draft was not saved atomically.");
  File.WriteAllText(Path.Combine(root, "recovery-result.txt"), "PASS: forced termination, actual recovery dialog and save.");
 }

 private async Task RunMetadataCrashSmokeAsync(string directory)
 {
  foreach (var encrypted in new[] { false, true })
  {
   var root = Path.Combine(directory, encrypted ? "metadata-crash-encrypted" : "metadata-crash-plain");
   Directory.CreateDirectory(root);
   File.WriteAllText(Path.Combine(root, "test-owner.txt"), MetadataCrashProbe.Owner);
   var database = Path.Combine(root, "catalog", "catalog.db");
   using (var db = new CatalogRepository(database)) db.Upsert(new() { Id = MetadataCrashProbe.MediaId, Path = Path.Combine(root, "synthetic.mp4"), Title = "Synthetic crash fixture", Kind = MediaKind.Video });
   if (encrypted) CatalogEncryption.EnableEncryption(database, MetadataCrashProbe.Password);
   foreach (var phase in new[] { "write", "recover", "role", "role-light" })
   {
    var required=phase.StartsWith("role",StringComparison.Ordinal)?new[]{"role-result.txt","image-set-restore-result.txt","image-set-crud-result.txt","publication-restore-result.txt","publication-crud-result.txt","hierarchy-host-result.txt"}:new[]{phase=="write"?"kill-marker.txt":"recovery-result.txt"};
    foreach(var name in required.Append("result.txt"))File.Delete(Path.Combine(root,name));
    File.WriteAllText(Path.Combine(root, "phase.txt"), phase);
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
    start.ArgumentList.Add("--draft-crash-test"); start.ArgumentList.Add(root);
    using var child = Process.Start(start) ?? throw new IOException("Synthetic metadata child did not start.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    try { await child.WaitForExitAsync(timeout.Token); }
    finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
    if ((phase=="write"?child.ExitCode==0:child.ExitCode!=0)||required.Any(name=>!File.Exists(Path.Combine(root,name))))
     throw new InvalidOperationException("Synthetic metadata crash/recovery child failed: "+phase+(File.Exists(Path.Combine(root,"result.txt"))?"\n"+File.ReadAllText(Path.Combine(root,"result.txt")):""));
   }
  }
 }
}
