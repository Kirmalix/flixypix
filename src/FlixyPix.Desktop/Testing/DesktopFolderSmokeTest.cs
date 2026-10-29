using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Library folders: only watched folders are watched, a new file shows the banner, the window lists it and
 /// taking it over imports it; a removed file is not offered again.</summary>
 private async Task RunLibraryFolderSmokeTestAsync(string directory, string fixtureDirectory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  var watched = Path.Combine(directory, "library-folder"); var unwatched = Path.Combine(directory, "library-folder-unwatched");
  Directory.CreateDirectory(watched); Directory.CreateDirectory(unwatched);
  var sample = Directory.EnumerateFiles(fixtureDirectory, "*.png").First();
  var settle = FolderSettleTime; FolderSettleTime = TimeSpan.FromMilliseconds(300);
  try
  {
   _repository.SaveLibraryFolders([new LibraryFolder { Path = watched, Category = "Ordnertest" }, new LibraryFolder { Path = unwatched, Watch = false }]);
   RestartFolderWatcher();
   Require(FolderWatcher?.Watched.SequenceEqual([watched], StringComparer.OrdinalIgnoreCase) == true, "Only the folder marked for watching may be watched.");
   await CheckLibraryFoldersAsync(); Require(!FolderBannerShown, "The banner appeared without changes.");
   File.Copy(sample, Path.Combine(unwatched, "Unbeobachtet.png"));
   var arrived = Path.Combine(watched, "Neu im Ordner.png"); File.Copy(sample, arrived);
   for (var waited = 0; !FolderBannerShown && waited < 100; waited++) await Task.Delay(100);
   Require(FolderBannerShown, "A new file in a watched folder did not show the banner.");
   Require(FolderBannerText.Text.Contains(Tn(2, "1 new file", "{0:N0} new files"), StringComparison.Ordinal), "The banner did not count the new files: " + FolderBannerText.Text);
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Capture(RootContent, Path.Combine(directory, "desktop-folder-banner.png"));
   FolderBannerReview(this, new RoutedEventArgs());
   var window = _foldersWindow ?? throw new InvalidOperationException("The library folder window did not open.");
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Require(window.Rows.Count == 2 && window.Rows.All(r => r.Change.Kind == FolderChangeKind.New && r.Chosen), "The window did not list the new files ready to take over.");
   Capture((FrameworkElement)window.Content, Path.Combine(directory, "library-folders.png"));
   window.Rows.Single(r => r.Name == "Unbeobachtet.png").Chosen = false;
   await window.ApplyAsync();
   var item = _repository.GetMediaByPath(arrived) ?? throw new InvalidOperationException("Taking over did not import the new file.");
   Require(item.Category == "Ordnertest" && _repository.GetMediaByPath(Path.Combine(unwatched, "Unbeobachtet.png")) == null, "Folder category or the declined row was not honoured.");
   window.Close();
   _repository.Delete(item.Id); ForgetInLibraryFolders(item.Path);
   await CheckLibraryFoldersAsync();
   Require(_folderPlan?.Actionable.Count == 1, "A removed file was offered again or the declined file vanished.");
  }
  finally
  {
   FolderSettleTime = settle;
   _repository.SaveLibraryFolders([]); RestartFolderWatcher(); HideFolderBanner();
  }
 }
}
