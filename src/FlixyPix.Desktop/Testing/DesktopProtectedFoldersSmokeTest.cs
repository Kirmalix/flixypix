using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Protected folders are chosen in the settings, take effect on saving and can never cover FlixyPix's own data.</summary>
 private async Task RunProtectedFoldersSmokeTestAsync(string directory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  var source = Path.Combine(directory, "protected-source"); Directory.CreateDirectory(source);
  var settings = new SettingsWindow(this, _repository, _settings, _settings.DataDirectory, () => { }, () => { }); settings.Show();
  try
  {
   settings.SelectPage("Storage");
   Require(!settings.AddProtectedFolder(AppStorage.Current.Root) && settings.LastError != null, "The storage folder of FlixyPix could be protected.");
   Require(!settings.AddProtectedFolder(_settings.DataDirectory), "The open library could be protected.");
   Require(settings.AddProtectedFolder(source), "An ordinary folder could not be protected: " + settings.LastError);
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Capture((FrameworkElement)settings.Content, Path.Combine(directory, "settings-protected-folders.png"));
   Require(settings.TrySave() && settings.Result?.ProtectedFolders is [var saved] && saved == source, "The settings did not keep the protected folder.");
   _settings = _settings with { ProtectedSourcePaths = [source] }; SourceProtection.Apply(_settings);
   Require(FileAccessPolicy.Current.IsWriteProtected(Path.Combine(source, "file.mp4")), "The protected folder is still writable.");
   Require(!FileAccessPolicy.Current.IsWriteProtected(Path.Combine(_settings.DataDirectory, "file.tmp")), "The library became write-protected.");
   // Stored settings may name a folder that holds the library; it must not stop FlixyPix from working.
   _settings = _settings with { ProtectedSourcePaths = [source, _settings.DataDirectory] }; SourceProtection.Apply(_settings);
   Require(FileAccessPolicy.Current.IsWriteProtected(source) && !FileAccessPolicy.Current.IsWriteProtected(_settings.DataDirectory), "A stored protection of the library took effect.");
  }
  finally { settings.CloseDiscarding(); _settings = _settings with { ProtectedSourcePaths = [] }; SourceProtection.Apply(_settings); }
 }
}
