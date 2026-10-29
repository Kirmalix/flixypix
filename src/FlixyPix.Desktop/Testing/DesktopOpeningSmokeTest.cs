using System.IO;
using System.Windows;
using System.Windows.Threading;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>A library in an older format: the question with duration and room, then the conversion with its progress
 /// window, and the notice that the catalog can be compacted.</summary>
 private async Task RunOpeningSmokeTestAsync(string directory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  async Task Settled() { await _lastRefresh; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
  var library = Path.Combine(directory, "older-library"); Directory.CreateDirectory(library);
  var database = Path.Combine(library, "catalog.db");
  using (var old = new CatalogRepository(database))
  {
   for (var index = 0; index < 3; index++) old.Upsert(new() { Path = Path.Combine(library, $"clip{index}.mp4"), Kind = MediaKind.Video, Title = $"Clip {index}" });
  }
  using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
  { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=15"; command.ExecuteNonQuery(); }

  var plan = CatalogRepository.Inspect(database);
  Require(plan.Upgrades && plan.HasRoom && plan.Media == 3 && plan.Phases.SequenceEqual([CatalogOpenPhase.SafetyCopy, CatalogOpenPhase.Entries]),
   "The plan for an older library is wrong: " + string.Join(", ", plan.Phases));
  var question = LibraryOpeningWindow.Preview(this, database, plan); question.Show();
  try { await Settled(); Capture((FrameworkElement)question.Content, Path.Combine(directory, "library-upgrade-question.png")); }
  finally { question.Close(); }

  var progressShown = false;
  LibraryOpeningWindow.ConfirmAutomatically = true;
  LibraryOpeningWindow.ProgressShown = window => { progressShown = true; window.UpdateLayout(); Capture((FrameworkElement)window.Content, Path.Combine(directory, "library-upgrade-progress.png")); };
  try
  {
   using var opened = SecretDialogs.OpenLibrary(this, database);
   Require(progressShown && opened.UpgradedOnOpen && opened.Search().Count == 3 && opened.GetMaintenanceLog().LastUpgradeFrom == 15,
    "The older library was not converted through the progress window.");
  }
  finally { LibraryOpeningWindow.ConfirmAutomatically = false; LibraryOpeningWindow.ProgressShown = null; }
  // Opened again, nothing is left to do: no window, no question.
  Require(!CatalogRepository.Inspect(database).HasWork, "The converted library still wants work.");
 }
}
