using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Message window, notifications and the shared frame of secondary windows render in the colour scheme and behave:
 /// default and cancelling choice, at most three notifications, a notification under the pointer stays.</summary>
 private async Task RunNotificationSmokeTestAsync(string directory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  async Task Idle() => await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  var question = new MessageWindow(this, "Soll die Auswahl aus dem Katalog entfernt werden?\nDie Dateien bleiben auf dem Datenträger.", "Medien entfernen",
   MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
  question.Show(); await Idle();
  Require(question.Choices.Select(b => (string)b.Content).SequenceEqual([T("Yes"), T("No"), T("Cancel")]), "Message choices are not in German or out of order.");
  Require(question.Choices.Single(b => b.IsDefault).Content as string == T("No") && question.Choices.Single(b => b.IsCancel).Content as string == T("Cancel"), "Default or cancelling choice is wrong.");
  Capture((FrameworkElement)question.Content, Path.Combine(directory, "message-window.png"));
  question.Close();

  foreach (var (text, kind) in new[] { ("Vorschaubilder anpassen · abgeschlossen", MessageBoxImage.Information), ("Medien importieren · abgebrochen", MessageBoxImage.Warning),
   ("Sicherung gespeichert", MessageBoxImage.Information), ("Übernahme · abgeschlossen", MessageBoxImage.Information) })
   ShowToast(text, kind, kind == MessageBoxImage.Warning ? "Erneut versuchen" : null, kind == MessageBoxImage.Warning ? () => { } : null, seconds: 30);
  await Idle(); await Task.Delay(250);
  Require(VisibleToasts.Count() == 3 && VisibleToasts.First() == "Medien importieren · abgebrochen", "Notifications did not keep the newest three.");
  Capture(RootContent, Path.Combine(directory, "notifications.png"));
  _toasts.Children.Clear();

  var report = Dialogs.TextWindow(this, "Importbericht", "3 Dateien übernommen.\n1 Datei übersprungen: kein unterstütztes Format.");
  report.Show(); await Idle();
  Capture((FrameworkElement)report.Content, Path.Combine(directory, "text-window.png"));
  report.Close();

  // Encryption dialogs: password entry and the recovery key that cannot be closed before it is confirmed.
  var (passwordDialog, inputs) = SecretDialogs.PasswordWindow(this, "Bibliothek verschlüsseln", "Das Passwort wird nirgends gespeichert.", "Neues Passwort", "Passwort wiederholen");
  passwordDialog.Show(); await Idle();
  Require(inputs.Count == 2, "The password dialog did not show both fields.");
  Capture((FrameworkElement)passwordDialog.Content, Path.Combine(directory, "encryption-password.png")); passwordDialog.Close();
  var (keyDialog, stored, done) = SecretDialogs.RecoveryKeyWindow(this, "ABCD-EFGH-IJKL-MNOP-QRST-UVWX");
  keyDialog.Show(); await Idle();
  Require(!done.IsEnabled, "The recovery key could be confirmed without ticking the box.");
  keyDialog.Close(); await Idle(); Require(keyDialog.IsVisible, "The recovery key closed before it was confirmed.");
  Capture((FrameworkElement)keyDialog.Content, Path.Combine(directory, "encryption-recovery-key.png"));
  stored.IsChecked = true; Require(done.IsEnabled, "Ticking the box did not enable confirming."); keyDialog.Close();

  // A background task whose failure nobody awaited ends up in the error log of the test run.
  _ = Task.Run(() => throw new InvalidOperationException("Protokollprobe")); await Task.Delay(200);
  for (var round = 0; round < 10 && !(File.Exists(ErrorLog.TodayFile) && File.ReadAllText(ErrorLog.TodayFile).Contains("Protokollprobe")); round++)
  { GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(100); }
  Require(File.Exists(ErrorLog.TodayFile) && File.ReadAllText(ErrorLog.TodayFile).Contains("Hintergrundaufgabe") && ErrorLog.TodayFile.StartsWith(directory, StringComparison.OrdinalIgnoreCase),
   "An unobserved task failure was not written to the test run's error log.");

  // NFO preview and takeover for a synthetic video with a Kodi file beside it.
  var folder = Path.Combine(directory, "nfo"); Directory.CreateDirectory(folder);
  var videoPath = Path.Combine(folder, "reise.mp4"); File.WriteAllBytes(videoPath, [0]);
  File.WriteAllText(Path.Combine(folder, "reise.nfo"), "<movie><title>Die große Reise</title><year>2021</year><genre>Abenteuer</genre><actor><name>Max Roth</name></actor><plot>Über die Alpen.</plot><thumb>reise-poster.png</thumb><fanart><thumb>http://example.invalid/fanart.jpg</thumb></fanart></movie>");
  var poster = new System.Windows.Media.Imaging.RenderTargetBitmap(32, 48, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); var posterEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
  posterEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(poster)); using (var posterFile = File.Create(Path.Combine(folder, "reise-poster.png"))) posterEncoder.Save(posterFile);
  var video = _repository.Upsert(new FlixyPix.Core.MediaItem { Path = videoPath, Title = "reise", Kind = FlixyPix.Core.MediaKind.Video });
  try
  {
   var plan = _repository.PlanNfoImport([video.Id]);
   var preview = new NfoPreviewWindow(this, plan, "1 ausgewähltes Video"); preview.Show(); await Idle();
   Require(preview.RowCount == 1 && preview.Chosen.Single().Tags.ContainsKey(FlixyPix.Core.CatalogRepository.NfoLabel("genre")), "The NFO preview did not list the change.");
   Require(plan.SkippedLinks == 1 && preview.Chosen.Single().Images.Single() is { IsFanart: false }, "The NFO preview did not plan the local thumbnail and skip the web address.");
   Capture((FrameworkElement)preview.Content, Path.Combine(directory, "nfo-preview.png"));
   var chosen = preview.Chosen; preview.Close();
   Require(_repository.ApplyNfoImport(chosen) == 1 && _repository.GetMedia(video.Id)!.Title == "Die große Reise", "The NFO data were not taken over.");
   await ShowStoredThumbnailAsync(video.Id);
   Require(_repository.GetMedia(video.Id)!.ThumbnailPath is { } shown && File.Exists(shown) && shown.StartsWith(_settings.DataDirectory, StringComparison.OrdinalIgnoreCase),
    "The thumbnail from the NFO file was not shown.");
  }
  finally { _repository.Delete(video.Id); }
 }
}
